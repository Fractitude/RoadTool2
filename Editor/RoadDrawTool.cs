using System;
using System.Collections.Generic;
using Editor;
using Sandbox;

namespace RedSnail.RoadTool2.Editor;

/// <summary>
/// Click on the ground to lay road nodes; consecutive clicks are joined by segments. Clicking an existing node
/// continues from it or joins the road to it; clicking on an existing road splits it there and does the same, which
/// is how roads get branched off and merged into each other. Right-click or Escape ends the chain.
/// </summary>
[Title("Draw")]
[Icon("timeline")]
[Group("1")]
[Order(0)]
public class RoadDrawTool : EditorTool
{
	/// <summary>
	/// A right button release this close to where it was pressed is a click; anything further is the camera orbiting.
	/// </summary>
	private const float ClickDragTolerance = 4.0f;

	/// <summary>
	/// A split this close to a road's end reuses the end node instead of leaving a sliver of road behind.
	/// </summary>
	private const float MinEndDistance = 25.0f;

	/// <summary>
	/// What the cursor is over this frame and where the next node would go.
	/// </summary>
	private readonly struct Target
	{
		public readonly Vector3 World;
		public readonly RoadNode Node;
		public readonly RoadSegmentPick? Road;

		/// <summary>
		/// The parking lot the point opens into, when the cursor is over one: the road starts or ends on its edge.
		/// </summary>
		public readonly RoadLot Lot;

		public Target(Vector3 _World, RoadNode _Node, RoadSegmentPick? _Road, RoadLot _Lot = null)
		{
			World = _World;
			Node = _Node;
			Road = _Road;
			Lot = _Lot;
		}
	}

	/// <summary>
	/// The direction the road being drawn snapped to, from the node being extended, and the angle the guide shows
	/// for it. In World space (the editor's Global/Local toggle) that is the heading round from the world X axis,
	/// the yaw of something facing down the road. In Local space it is the angle to the road at the node it was
	/// measured from, read like a protractor laid on the node: 90° square to that road, 180° straight on. Either
	/// way the number is exact: the road is built at precisely that angle, not at something it rounds to.
	/// </summary>
	internal readonly struct SnapGuide
	{
		public readonly Vector3 Start;

		/// <summary>
		/// Degrees round from the world X axis, in [0, 360).
		/// </summary>
		public readonly float Heading;

		/// <summary>
		/// The heading of the road leaving the node that the angle is measured from; null when it is measured from
		/// the world X axis.
		/// </summary>
		public readonly float? Reference;

		public SnapGuide(Vector3 _Start, float _Heading, float? _Reference)
		{
			Start = _Start;
			Heading = _Heading;
			Reference = _Reference;
		}

		public float Shown => Reference is { } reference ? MathF.Abs(AngleDifference(Heading, reference)) : Heading;

		/// <summary>
		/// Whether a point lies on the guide's line, ahead of the node, so a road to it runs at the guide's angle.
		/// The tolerance only covers the rounding of the arithmetic that put a point there.
		/// </summary>
		public bool Holds(Vector3 _World)
		{
			Vector3 along = HeadingDirection(Heading);
			var direction = new Vector2(along.x, along.y);
			var offset = new Vector2(_World.x - Start.x, _World.y - Start.y);

			return Vector2.Dot(offset, direction) > 0.0f && MathF.Abs(Cross(direction, offset)) < 0.1f;
		}
	}

	[Property, Title("Width"), Range(50.0f, 2000.0f)] public float Width { get; set; } = 500.0f;

	/// <summary>
	/// The road type new roads get. Empty uses the network's default profile.
	/// </summary>
	[Property, Title("Profile")] public RoadProfile Profile { get; set; }

	/// <summary>
	/// Set, new roads are drawn as bridges in this style. The Edit tool changes it per road afterwards.
	/// </summary>
	[Property, Title("Bridge")] public BridgeStyle Bridge { get; set; }

	/// <summary>
	/// What new roads get standing along them (lamp posts, trees...). The Edit tool changes it per road afterwards.
	/// </summary>
	[Property, Title("Props")] public RoadPropSet Props { get; set; }

	[Property, Title("Snap Radius"), Range(0.0f, 500.0f)] public float SnapRadius { get; set; } = 150.0f;

	/// <summary>
	/// Lanes new roads get: two-way with the odd lane running the way the road is drawn, or one-way that way.
	/// The Lanes tool changes them afterwards.
	/// </summary>
	[Property, Title("Lanes"), Range(RoadSegment.MinLanes, RoadSegment.MaxLanes)] public int Lanes { get; set { field = value.Clamp(RoadSegment.MinLanes, RoadSegment.MaxLanes); } } = 2;

	[Property, Title("One Way")] public bool OneWay { get; set; } = false;

	/// <summary>
	/// Sharp corners give straight roads meeting at an angle (grid streets); off, the road flows smoothly through
	/// every node placed.
	/// </summary>
	[Property, Title("Sharp Corners")] public bool SharpCorners { get; set; } = false;

	/// <summary>
	/// Snap the direction of the road being drawn to whole steps of <see cref="AngleStep"/>: round from the world
	/// axes in World space (the editor's Global/Local toggle), or round from the roads already at the node being
	/// extended in Local space. The angle shown while placing is exactly the one the road is built at. Holding Shift
	/// flips it for one placement.
	/// </summary>
	[Property, Title("Angle Snap")] public bool AngleSnap { get; set; } = false;

	[Property, Title("Snap Angle"), Range(1.0f, 90.0f)] public float AngleStep { get; set; } = 10.0f;

	/// <summary>
	/// How far the snapped direction is drawn out as a guide while placing.
	/// </summary>
	private const float GuideLength = 4000.0f;

	private int m_LastNodeId = -1;
	private Vector2 m_RightPressPosition;

	/// <summary>
	/// The direction the current placement snapped to, for the guide; null when not snapping, or when the end has
	/// been pulled off the snapped line (onto a node beside it) and the road no longer runs at the angle.
	/// </summary>
	private SnapGuide? m_Snap;

	/// <summary>
	/// The node the current placement is aligned with, for the guide; null when not aligned.
	/// </summary>
	private Vector3? m_AlignAnchor;

	private RoadNetworkTool Root => Manager?.CurrentTool as RoadNetworkTool;

	public bool IsDrawing => m_LastNodeId >= 0;

	private bool SnapActive => AngleSnap != Gizmo.IsShiftPressed;



	public override void OnDisabled()
	{
		EndChain();
	}



	public override Widget CreateToolSidebar()
	{
		return new RoadDrawToolWidget(this);
	}



	public override void OnUpdate()
	{
		var root = Root;

		if (root is null)
			return;

		var network = root.Network;

		// Undo can pull the node we were extending from out from under us.
		if (IsDrawing && (!network.IsValid() || network.FindNode(m_LastNodeId) is null))
			EndChain();

		if (!Gizmo.HasMouseFocus)
			return;

		if (Gizmo.WasRightMousePressed)
			m_RightPressPosition = Gizmo.CursorPosition;

		if (Gizmo.WasRightMouseReleased && Vector2.Distance(Gizmo.CursorPosition, m_RightPressPosition) < ClickDragTolerance)
			EndChain();

		if (!TryGetCursorPosition(network, out Vector3 cursorWorld))
			return;

		var target = ResolveTarget(network, cursorWorld);

		m_Snap = null;
		m_AlignAnchor = null;

		// Joining a node is explicit and wins. Otherwise the snap places the end: on bare ground, or on a road the
		// snapped line crosses (the hovered road above all), which is split right there so the merge is straight.
		if (target.Node is null && IsDrawing && SnapActive && TrySnapAngle(network, cursorWorld, target.Road, out Vector3 snappedWorld, out SnapGuide guide, out RoadSegmentPick? split))
		{
			if (split is { } pick)
			{
				target = new Target(snappedWorld, null, pick);
				m_Snap = guide;
			}
			else if (target.Road is null)
			{
				// The snapped end landing on a node joins it, like clicking the node would; landing on a parking
				// lot opens into it, where the snapped line crosses its edge.
				var joined = network.FindNearestNode(network.WorldTransform.PointToLocal(snappedWorld), SnapRadius);

				if (joined is not null && joined.Id != m_LastNodeId)
					target = new Target(network.WorldTransform.PointToWorld(joined.Position), joined, null);
				else if (LotUnder(network, network.WorldTransform.PointToLocal(snappedWorld)) is { } snappedLot)
					target = new Target(EdgeWorld(network, snappedLot, snappedWorld, guide.Start), null, null, snappedLot);
				else
					target = new Target(snappedWorld, null, null);

				m_Snap = guide;
			}
			else
			{
				// Hovering a road the snapped line never reaches: the plain join, and no alignment guide.
				m_AlignAnchor = null;
			}
		}

		target = AtRoadEnd(network, target);

		// The guide promises the road runs at its angle, so it goes as soon as the end leaves the line: joining a
		// node beside it, or a split by a road's end taking that end's node.
		if (m_Snap is { } snap && !snap.Holds(target.World))
		{
			m_Snap = null;
			m_AlignAnchor = null;
		}

		DrawPreview(network, target);

		if (Gizmo.WasLeftMousePressed)
			PlaceNode(root, target);
	}



	/// <summary>
	/// Snaps the direction from the node being extended to the cursor onto the nearest of the snap angles. In
	/// World space (the editor's Global/Local toggle) those are whole steps round from the world X axis, so 45° is
	/// 45° in the world. In Local space they are whole steps round from each road already leaving that node, and
	/// from straight on through it, so a branch is exactly square to, or straight on from, a road at any angle; a
	/// node no road leaves yet has nothing of its own to measure from and falls back on the world axes, as the
	/// Select tool's gizmo does. The cursor's distance along the snapped direction is kept and the point is dropped
	/// back onto the ground there.
	/// </summary>
	private bool TrySnapAngle(RoadNetwork _Network, Vector3 _CursorWorld, RoadSegmentPick? _Hovered, out Vector3 _SnappedWorld, out SnapGuide _Guide, out RoadSegmentPick? _Split)
	{
		_SnappedWorld = _CursorWorld;
		_Guide = default;
		_Split = null;

		if (!_Network.IsValid() || _Network.FindNode(m_LastNodeId) is not { } last)
			return false;

		var transform = _Network.WorldTransform;
		Vector3 startWorld = transform.PointToWorld(last.Position);
		var delta = new Vector2(_CursorWorld.x - startWorld.x, _CursorWorld.y - startWorld.y);

		if (delta.Length < 1.0f)
			return false;

		float step = MathF.Max(1.0f, AngleStep);
		float raw = MathF.Atan2(delta.y, delta.x).RadianToDegree();
		var headings = new List<float>();

		if (!Gizmo.Settings.GlobalSpace)
		{
			foreach (Vector2 road in RoadDirections(_Network, last))
				headings.Add(MathF.Atan2(road.y, road.x).RadianToDegree());
		}

		_Guide = headings.Count > 0
			? SnapToRoads(startWorld, raw, headings, step)
			: new SnapGuide(startWorld, NormalizeAngle(SnapTo(raw, 0.0f, step)), null);

		Vector3 along = HeadingDirection(_Guide.Heading);
		var direction = new Vector2(along.x, along.y);
		var start = new Vector2(startWorld.x, startWorld.y);
		float length = MathF.Max(1.0f, Vector2.Dot(delta, direction));
		float tolerance = AlignTolerance(_CursorWorld);

		// A road the snapped line crosses near the end splits there: the merge comes out dead straight. The road
		// under the cursor is what the user is aiming at, so it takes the crossing from much further off.
		if (TryCrossRoad(_Network, start, direction, length, tolerance, _Hovered, out RoadSegmentPick pick, out Vector3 crossingWorld))
		{
			_SnappedWorld = crossingWorld;
			_Split = pick;

			// Lining up with a node as well is only a coincidence here; the guide would mislead.
			m_AlignAnchor = null;

			return true;
		}

		if (TryAlign(_Network, last, start, direction, length, tolerance, out float aligned, out Vector3 anchor))
		{
			length = aligned;
			m_AlignAnchor = anchor;
		}

		Vector2 snapped = start + direction * length;

		_SnappedWorld = new Vector3(snapped.x, snapped.y, GroundHeightAt(Scene, snapped, _CursorWorld.z));

		return true;
	}



	/// <summary>
	/// Where the snapped line crosses a road's centreline, when that crossing is within the tolerance of the
	/// cursor's distance along the line, or, for the road under the cursor, within a couple of road widths of it.
	/// The nearest such crossing wins.
	/// </summary>
	private static bool TryCrossRoad(RoadNetwork _Network, Vector2 _Start, Vector2 _Direction, float _Length, float _Tolerance, RoadSegmentPick? _Hovered, out RoadSegmentPick _Pick, out Vector3 _World)
	{
		var transform = _Network.WorldTransform;
		float bestError = float.MaxValue;
		bool found = false;

		_Pick = default;
		_World = default;

		foreach (var segment in _Network.Segments)
		{
			var table = _Network.GetCurveTable(segment);

			if (table is null)
				continue;

			bool hovered = _Hovered is { } hover && hover.Segment.Id == segment.Id;
			float allowed = hovered ? MathF.Max(_Tolerance, segment.Width * 2.0f) : _Tolerance;
			Vector3 previousWorld = transform.PointToWorld(table.SamplePosition(0));

			for (int i = 1; i < table.SampleCount; i++)
			{
				Vector3 currentWorld = transform.PointToWorld(table.SamplePosition(i));
				var p = new Vector2(previousWorld.x, previousWorld.y);
				var edge = new Vector2(currentWorld.x - previousWorld.x, currentWorld.y - previousWorld.y);
				float denominator = Cross(_Direction, edge);

				if (MathF.Abs(denominator) > 0.000001f)
				{
					Vector2 offset = p - _Start;
					float t = Cross(offset, edge) / denominator;
					float u = Cross(offset, _Direction) / denominator;

					// Past the start of the line and within this piece of the road.
					if (t >= 1.0f && u >= 0.0f && u <= 1.0f)
					{
						float error = MathF.Abs(t - _Length);

						if (error <= allowed && error < bestError)
						{
							// Where the road itself crosses the line, not the chord between its samples, so the split
							// lands exactly on the line.
							float samples = table.SampleCount - 1;
							float curveT = CrossingT(table.Curve, transform, _Start, _Direction, (i - 1) / samples, i / samples, (i - 1 + u) / samples);
							Vector3 local = table.Curve.Evaluate(curveT);

							bestError = error;
							found = true;
							_Pick = new RoadSegmentPick(segment, curveT, local);
							_World = transform.PointToWorld(local);
						}
					}
				}

				previousWorld = currentWorld;
			}
		}

		return found;
	}



	/// <summary>
	/// Where between two of its samples the road itself crosses a line in the plan, as a curve parameter. The
	/// samples lie either side of the line and the road between them is all but straight, so cutting the bracket
	/// where a straight road would cross closes in on it within a few steps. Without a bracket (the line runs
	/// through a sample, give or take rounding) the guess is as good as it gets.
	/// </summary>
	private static float CrossingT(in RoadCurve _Curve, in Transform _Transform, Vector2 _Start, Vector2 _Direction, float _From, float _To, float _Guess)
	{
		float from = _From;
		float to = _To;
		float sideFrom = SideOf(_Curve, _Transform, _Start, _Direction, from);
		float sideTo = SideOf(_Curve, _Transform, _Start, _Direction, to);

		if ((sideFrom < 0.0f) == (sideTo < 0.0f))
			return _Guess;

		float t = _Guess;
		int lastMoved = 0;

		for (int i = 0; i < 8; i++)
		{
			t = from + (to - from) * sideFrom / (sideFrom - sideTo);

			float side = SideOf(_Curve, _Transform, _Start, _Direction, t);

			if (MathF.Abs(side) < 0.001f)
				break;

			// The end that has not moved for two cuts running gets half its weight (the Illinois rule): a bend keeps
			// one end still, and the cuts would otherwise only creep up on the crossing from the other side.
			if ((side < 0.0f) == (sideFrom < 0.0f))
			{
				from = t;
				sideFrom = side;

				if (lastMoved == 1)
					sideTo *= 0.5f;

				lastMoved = 1;
			}
			else
			{
				to = t;
				sideTo = side;

				if (lastMoved == -1)
					sideFrom *= 0.5f;

				lastMoved = -1;
			}
		}

		return t;
	}



	/// <summary>
	/// How far a point of the road is to the left of a line in the plan.
	/// </summary>
	private static float SideOf(in RoadCurve _Curve, in Transform _Transform, Vector2 _Start, Vector2 _Direction, float _T)
	{
		Vector3 world = _Transform.PointToWorld(_Curve.Evaluate(_T));

		return Cross(_Direction, new Vector2(world.x - _Start.x, world.y - _Start.y));
	}



	/// <summary>
	/// With the direction fixed, the distance along it is the one thing left to place; this snaps it to where the
	/// road's line crosses a guide line through another node, when the cursor is close to that crossing. Guides
	/// through a node run square to the road being drawn (the end lines up level with that node), along the world
	/// axes in World space, and along the roads leaving that node (the end lands on that road's line). Grids come
	/// out exact.
	/// </summary>
	internal static bool TryAlign(RoadNetwork _Network, RoadNode _Last, Vector2 _Start, Vector2 _Direction, float _Length, float _Tolerance, out float _AlignedLength, out Vector3 _AnchorWorld)
	{
		var transform = _Network.WorldTransform;
		var perpendicular = new Vector2(-_Direction.y, _Direction.x);
		bool worldAxes = Gizmo.Settings.GlobalSpace;
		float bestError = _Tolerance;
		bool found = false;

		_AlignedLength = _Length;
		_AnchorWorld = default;

		foreach (var node in _Network.Nodes)
		{
			if (node.Id == _Last.Id)
				continue;

			Vector3 world = transform.PointToWorld(node.Position);
			var anchor = new Vector2(world.x, world.y);

			foreach (Vector2 guide in GuidesAt(_Network, node, perpendicular, worldAxes))
			{
				float denominator = Cross(_Direction, guide);

				// A guide nearly along the road crosses it nowhere useful.
				if (MathF.Abs(denominator) < 0.05f)
					continue;

				float crossing = Cross(anchor - _Start, guide) / denominator;

				if (crossing < 1.0f)
					continue;

				float error = MathF.Abs(crossing - _Length);

				if (error >= bestError)
					continue;

				bestError = error;
				_AlignedLength = crossing;
				_AnchorWorld = world;
				found = true;
			}
		}

		return found;
	}



	private static IEnumerable<Vector2> GuidesAt(RoadNetwork _Network, RoadNode _Node, Vector2 _Perpendicular, bool _WorldAxes)
	{
		yield return _Perpendicular;

		// The world's own axes only count in World space; in Local space everything is measured from the roads.
		if (_WorldAxes)
		{
			yield return new Vector2(1.0f, 0.0f);
			yield return new Vector2(0.0f, 1.0f);
		}

		foreach (Vector2 direction in RoadDirections(_Network, _Node))
			yield return direction;
	}



	/// <summary>
	/// Which way each road leaves a node, flat and in world space.
	/// </summary>
	internal static IEnumerable<Vector2> RoadDirections(RoadNetwork _Network, RoadNode _Node, int _ExcludeSegmentId = -1)
	{
		var transform = _Network.WorldTransform;

		foreach (var segment in _Network.GetSegmentsAt(_Node.Id))
		{
			if (segment.Id == _ExcludeSegmentId || !_Network.TryGetCurve(segment, out var curve))
				continue;

			Vector3 tangent = segment.StartNodeId == _Node.Id ? curve.Tangent(0.0f) : -curve.Tangent(1.0f);
			Vector3 worldTangent = transform.NormalToWorld(tangent);
			var flat = new Vector2(worldTangent.x, worldTangent.y);

			if (flat.Length > 0.001f)
				yield return flat.Normal;
		}
	}



	/// <summary>
	/// How close (in the world, at the cursor) the road's end has to be to a guide crossing to take it: a constant
	/// on-screen distance, like the node handles.
	/// </summary>
	internal static float AlignTolerance(Vector3 _World)
	{
		return MathF.Max(25.0f, Gizmo.Camera.Position.Distance(_World) * 0.025f);
	}



	internal static float Cross(Vector2 _A, Vector2 _B)
	{
		return _A.x * _B.y - _A.y * _B.x;
	}



	/// <summary>
	/// The multiple of the step, counted from the reference angle, nearest to the angle.
	/// </summary>
	internal static float SnapTo(float _Angle, float _Reference, float _Step)
	{
		return _Reference + MathF.Round(AngleDifference(_Angle, _Reference) / _Step) * _Step;
	}



	/// <summary>
	/// The snap angle nearest to an angle among whole steps round from each road leaving the node and from straight
	/// on through it, which differ when the step does not divide a half turn. The guide reads it out against the
	/// road it was measured from; when two roads give the same direction, against the one it makes the smaller
	/// angle with.
	/// </summary>
	internal static SnapGuide SnapToRoads(Vector3 _Start, float _Angle, IReadOnlyList<float> _Headings, float _Step)
	{
		float best = 0.0f;
		float bestError = float.MaxValue;
		float reference = 0.0f;

		foreach (float heading in _Headings)
		{
			foreach (float candidate in new[] { SnapTo(_Angle, heading, _Step), SnapTo(_Angle, heading + 180.0f, _Step) })
			{
				float error = MathF.Abs(AngleDifference(candidate, _Angle));
				bool same = bestError < float.MaxValue && MathF.Abs(AngleDifference(candidate, best)) < 0.001f;

				if (same ? MathF.Abs(AngleDifference(candidate, heading)) >= MathF.Abs(AngleDifference(best, reference)) : error >= bestError)
					continue;

				best = candidate;
				bestError = error;
				reference = heading;
			}
		}

		return new SnapGuide(_Start, NormalizeAngle(best), reference);
	}



	/// <summary>
	/// The signed shortest turn from one angle to the other, in (-180, 180].
	/// </summary>
	internal static float AngleDifference(float _To, float _From)
	{
		float difference = (_To - _From) % 360.0f;

		if (difference > 180.0f)
			difference -= 360.0f;
		else if (difference <= -180.0f)
			difference += 360.0f;

		return difference;
	}



	internal static float NormalizeAngle(float _Angle)
	{
		float normalized = _Angle % 360.0f;

		return normalized < 0.0f ? normalized + 360.0f : normalized;
	}



	/// <summary>
	/// The flat unit direction of a heading, in degrees round from the world X axis.
	/// </summary>
	internal static Vector3 HeadingDirection(float _Heading)
	{
		float radians = _Heading.DegreeToRadian();

		return new Vector3(MathF.Cos(radians), MathF.Sin(radians), 0.0f);
	}



	/// <summary>
	/// The ground height at a point in plan, from a trace straight down through it (roads skipped, like the cursor
	/// trace); the fallback height when there is nothing there.
	/// </summary>
	internal static float GroundHeightAt(Scene _Scene, Vector2 _Point, float _Fallback)
	{
		const float reach = 20000.0f;

		var top = new Vector3(_Point.x, _Point.y, _Fallback + reach * 0.5f);
		var ray = new Ray(top, Vector3.Down);

		var physics = _Scene.Trace.Ray(ray, reach).WithoutTags(RoadNetwork.ChunkTag).Run();

		if (physics.Hit)
			return RoadTerrainLayers.OntoGround(physics, ray).z;

		var meshes = _Scene.Trace.Ray(ray, reach).UseRenderMeshes(true, EditorPreferences.BackfaceSelection).WithoutTags("hidden").WithoutTags(RoadNetwork.ChunkTag).UsePhysicsWorld(false).Run();

		return meshes.Hit ? meshes.EndPosition.z : _Fallback;
	}



	public void EndChain()
	{
		m_LastNodeId = -1;
	}



	/// <summary>
	/// Nodes win over roads, roads over bare ground. A node counts as hit when its on-screen handle is under the
	/// cursor or it lies within the snap radius of the ground point.
	/// </summary>
	private Target ResolveTarget(RoadNetwork _Network, Vector3 _CursorWorld)
	{
		if (!_Network.IsValid())
			return new Target(_CursorWorld, null, null);

		var transform = _Network.WorldTransform;
		var node = RoadPicker.PickNode(_Network, Gizmo.CurrentRay);

		if (node is null && SnapRadius > 0.0f)
			node = _Network.FindNearestNode(transform.PointToLocal(_CursorWorld), SnapRadius);

		if (node is not null)
			return new Target(transform.PointToWorld(node.Position), node, null);

		var road = RoadPicker.PickSegment(_Network, Gizmo.CurrentRay);

		if (road is { } pick)
			return new Target(transform.PointToWorld(pick.LocalPoint), null, pick);

		// Over a parking lot: the road meets it on the nearest point of its edge.
		if (LotUnder(_Network, transform.PointToLocal(_CursorWorld)) is { } lot)
			return new Target(EdgeWorld(_Network, lot, _CursorWorld), null, null, lot);

		return new Target(_CursorWorld, null, null);
	}



	/// <summary>
	/// A split this close to a road's end reuses the end node instead (see <see cref="RoadNetwork.SplitSegment"/>),
	/// so the target becomes that node: the preview then shows the road that will really be built.
	/// </summary>
	private static Target AtRoadEnd(RoadNetwork _Network, in Target _Target)
	{
		if (_Target.Road is not { } pick || !_Network.IsValid() || _Network.GetCurveTable(pick.Segment) is not { } table)
			return _Target;

		float distance = pick.T * table.Length;
		int nodeId = distance <= MinEndDistance ? pick.Segment.StartNodeId : table.Length - distance <= MinEndDistance ? pick.Segment.EndNodeId : -1;

		if (nodeId < 0 || _Network.FindNode(nodeId) is not { } node)
			return _Target;

		return new Target(_Network.WorldTransform.PointToWorld(node.Position), node, null);
	}



	private void PlaceNode(RoadNetworkTool _Root, in Target _Target)
	{
		var session = SceneEditorSession.Active;
		var network = _Root.Network;
		bool createNetwork = !network.IsValid();

		// Picking up an existing node changes nothing in the graph (starting from it, clicking the node we are
		// already on, or joining one we are already connected to): just continue the chain from there.
		if (_Target.Node is { } existing && (!IsDrawing || existing.Id == m_LastNodeId || network.FindSegmentBetween(m_LastNodeId, existing.Id) is not null))
		{
			m_LastNodeId = existing.Id;
			return;
		}

		string undoName = _Target.Road is not null
			? (IsDrawing ? "Join Road" : "Branch Road")
			: (IsDrawing ? "Extend Road" : "Start Road");

		// A brand new network is undone by destroying its GameObject; an existing one by restoring its graph.
		var scope = session.UndoScope(undoName);

		if (createNetwork)
			scope.WithGameObjectCreations();
		else
			scope.WithComponentChanges(network);

		using (scope.Push())
		{
			if (createNetwork)
				network = _Root.CreateNetwork();

			RoadNode node;
			bool isNew = false;

			if (_Target.Node is not null)
			{
				node = _Target.Node;
			}
			else if (_Target.Road is { } pick)
			{
				node = network.SplitSegment(pick.Segment.Id, pick.T, MinEndDistance);
			}
			else if (_Target.Lot is { } lot)
			{
				// A new way into the lot, on its edge where the cursor was.
				node = network.AddOpening(lot.Id, network.WorldTransform.PointToLocal(_Target.World));
			}
			else
			{
				node = network.AddNode(network.WorldTransform.PointToLocal(_Target.World));
				isNew = true;
			}

			if (node is null)
				return;

			if (IsDrawing && node.Id != m_LastNodeId)
			{
				// A sharp corner needs both ends of the new road to hold the straight handles the road starts with,
				// so the smoothing must not touch either node: the one being extended from (it was placed
				// with smoothing on if the toggle changed mid-chain) any more than the new one. Handles the nodes
				// already have are left alone, so a road already running through is not kinked. A node fitting only
				// its handles' pitch never smooths them, so it stays as it is.
				if (SharpCorners)
				{
					StopSmoothing(network, m_LastNodeId);
					StopSmoothing(network, node.Id);
				}

				network.AddSegment(m_LastNodeId, node.Id, Width, Profile, Lanes, OneWay ? Lanes : RoadSegment.DefaultForwardLanes(Lanes), Bridge, Props);
			}

			// A corner node is a node whose handles all point straight at its neighbours.
			if (isNew && SharpCorners)
				network.SetTangentMode(node.Id, AutoTangentMode.Disabled);

			m_LastNodeId = node.Id;
		}
	}



	/// <summary>
	/// Takes a node off Full without touching its handles, so a road added to it keeps the straight handle it starts
	/// with.
	/// </summary>
	private static void StopSmoothing(RoadNetwork _Network, int _NodeId)
	{
		if (_Network.FindNode(_NodeId)?.TangentMode == AutoTangentMode.Full)
			_Network.SetTangentMode(_NodeId, AutoTangentMode.Disabled, false);
	}



	/// <summary>
	/// Where on a lot's edge a point over it belongs, in world space: the nearest point of the edge, or, for a road
	/// coming from outside the lot, where its line from there to the point crosses the edge, which keeps a snapped
	/// road at its angle.
	/// </summary>
	private static Vector3 EdgeWorld(RoadNetwork _Network, RoadLot _Lot, Vector3 _World, Vector3? _FromWorld = null)
	{
		var transform = _Network.WorldTransform;

		if (_FromWorld is { } fromWorld)
		{
			// In the lot's own coordinates the lot is a box round the origin, with the point inside it.
			Vector2 from = _Lot.Local(transform.PointToLocal(fromWorld));
			Vector2 to = _Lot.Local(transform.PointToLocal(_World));

			if (TryEnterBox(from, to, _Lot.HalfWidth, _Lot.HalfDepth, out Vector2 entry))
				return transform.PointToWorld(_Lot.Point(entry.x, entry.y));
		}

		int edge = _Lot.NearestEdge(transform.PointToLocal(_World), out float along);

		return transform.PointToWorld(_Lot.EdgePoint(edge, along));
	}



	/// <summary>
	/// Where a run from a point outside a box round the origin to a point inside it crosses into the box. The run
	/// has to be between both pairs of opposite sides at once, so it gets in as it crosses the later of the sides
	/// it starts beyond. False when it starts inside.
	/// </summary>
	private static bool TryEnterBox(Vector2 _From, Vector2 _To, float _HalfX, float _HalfY, out Vector2 _Entry)
	{
		Vector2 run = _To - _From;
		float enter = 0.0f;
		bool outside = false;

		if (MathF.Abs(_From.x) > _HalfX)
		{
			enter = MathF.Max(enter, (MathF.CopySign(_HalfX, _From.x) - _From.x) / run.x);
			outside = true;
		}

		if (MathF.Abs(_From.y) > _HalfY)
		{
			enter = MathF.Max(enter, (MathF.CopySign(_HalfY, _From.y) - _From.y) / run.y);
			outside = true;
		}

		_Entry = _From + run * enter;

		return outside;
	}



	/// <summary>
	/// The parking lot a point in the network's space lies on, or just outside: a road drawn there opens into it.
	/// </summary>
	private static RoadLot LotUnder(RoadNetwork _Network, Vector3 _Local)
	{
		foreach (var lot in _Network.Lots)
		{
			Vector2 local = lot.Local(_Local);

			if (MathF.Abs(local.x) <= lot.HalfWidth && MathF.Abs(local.y) <= lot.HalfDepth)
				return lot;
		}

		return null;
	}



	private bool TryGetCursorPosition(RoadNetwork _Network, out Vector3 _WorldPosition)
	{
		// The road meshes are skipped so a node placed beside a road lands on the ground, not on the road's edge; on a
		// terrain, on the ground the roads are laid over, not in another road's cut.
		var physics = Trace.WithoutTags(RoadNetwork.ChunkTag).Run();

		if (physics.Hit)
		{
			_WorldPosition = RoadTerrainLayers.OntoGround(physics, Gizmo.CurrentRay);
			return true;
		}

		var meshes = MeshTrace.WithoutTags(RoadNetwork.ChunkTag).Run();

		if (meshes.Hit)
		{
			_WorldPosition = meshes.EndPosition;
			return true;
		}

		// Nothing under the cursor (empty scene, or aiming past the terrain): fall back to a ground plane through
		// the node being extended so the road can still be drawn.
		float height = 0.0f;

		if (IsDrawing && _Network.IsValid() && _Network.FindNode(m_LastNodeId) is { } last)
			height = _Network.WorldTransform.PointToWorld(last.Position).z;

		var plane = new Plane(new Vector3(0.0f, 0.0f, height), Vector3.Up);
		var hit = plane.Trace(Gizmo.CurrentRay, true);

		_WorldPosition = hit ?? default;

		return hit.HasValue;
	}



	private void DrawPreview(RoadNetwork _Network, in Target _Target)
	{
		using (Gizmo.Scope("roadtool2_draw"))
		{
			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.LineThickness = 2.0f;

			if (_Target.Node is not null)
			{
				Gizmo.Draw.Color = Color.Green;
				RoadGizmos.DrawNode(_Target.World, true);
			}
			else if (_Target.Road is { } pick)
			{
				// Hovering a road: show the road and where the new node would split it.
				Gizmo.Draw.Color = Color.Orange.WithAlpha(0.6f);

				if (_Network.TryGetCurve(pick.Segment, out var roadCurve))
					RoadGizmos.DrawRoadOutline(roadCurve, _Network.WidthOf(pick.Segment), _Network.WorldTransform);

				Gizmo.Draw.Color = Color.Orange;
				RoadGizmos.DrawNode(_Target.World, true);
			}
			else
			{
				Gizmo.Draw.Color = Color.White;
				RoadGizmos.DrawNode(_Target.World, false);
			}

			if (!IsDrawing || !_Network.IsValid())
				return;

			if (_Target.Node is not null && _Target.Node.Id == m_LastNodeId)
				return;

			var transform = _Network.WorldTransform;
			Vector3 targetLocal = _Target.Node?.Position ?? _Target.Road?.LocalPoint ?? transform.PointToLocal(_Target.World);
			var curve = _Network.PreviewCurve(m_LastNodeId, targetLocal, _Target.Node?.Id ?? -1, SharpCorners);

			Gizmo.Draw.Color = Color.White.WithAlpha(0.9f);
			RoadGizmos.DrawRoadOutline(curve, Width, transform);

			Gizmo.Draw.Color = Color.White.WithAlpha(0.4f);
			RoadGizmos.DrawCurve(curve, transform);

			if (m_Snap is { } snap)
			{
				// The snapped direction, drawn out past the target, with the angle it landed on.
				Gizmo.Draw.LineThickness = 1.0f;
				Gizmo.Draw.Color = Color.Cyan.WithAlpha(0.5f);
				Gizmo.Draw.Line(snap.Start, snap.Start + HeadingDirection(snap.Heading) * GuideLength);

				// In Local space, what the angle is measured from: the road leaving the node, and an arc from it round
				// to the new road, kept a steady size on screen and inside the road being drawn.
				if (snap.Reference is { } reference)
				{
					float reach = new Vector2(_Target.World.x - snap.Start.x, _Target.World.y - snap.Start.y).Length;
					float radius = MathF.Min(Gizmo.Camera.Position.Distance(snap.Start) * 0.06f, reach * 0.5f);

					Gizmo.Draw.Line(snap.Start, snap.Start + HeadingDirection(reference) * radius * 1.25f);
					DrawArc(snap.Start, radius, reference, snap.Heading);
				}

				// Whole degrees for a whole-degree step; decimals only show when the step has them.
				Gizmo.Draw.Color = Color.Cyan;
				Gizmo.Draw.ScreenText($"{snap.Shown:0.##}°", _Target.World, new Vector2(12.0f, -12.0f), size: 12.0f);
			}

			if (SnapActive && _Network.FindNode(m_LastNodeId) is { } last)
				DrawDistance(transform.PointToWorld(last.Position), _Target.World);

			if (m_AlignAnchor is { } anchor)
			{
				// The node the end is lined up with, and the line between them.
				Gizmo.Draw.LineThickness = 2.0f;
				Gizmo.Draw.Color = Color.Orange;
				DrawDashed(anchor, _Target.World);
				RoadGizmos.DrawNode(anchor, true);
			}
		}
	}



	/// <summary>
	/// A line between two nodes with its length written beside the middle of it, on the side that is up on screen.
	/// Screen text cannot be turned, so it stays level and is only pushed away from the line.
	/// </summary>
	internal static void DrawDistance(Vector3 _From, Vector3 _To, int _Key = 0)
	{
		float length = Vector3.DistanceBetween(_From, _To);

		if (length < 0.01f)
			return;

		Gizmo.Draw.LineThickness = 1.5f;
		Gizmo.Draw.Color = Color.Yellow.WithAlpha(0.8f);
		Gizmo.Draw.Line(_From, _To);

		// A text drawn on the screen is one object per gizmo scope, so each label needs a scope of its own or the
		// next one replaces it.
		using var scope = Gizmo.Scope($"roadtool2_length_{_Key}");

		// The line as it lies on screen (y down).
		var from = Gizmo.Camera.ToScreen(_From);
		var to = Gizmo.Camera.ToScreen(_To);
		var onScreen = new Vector2(to.x - from.x, to.y - from.y);

		if (onScreen.Length < 0.001f)
			onScreen = new Vector2(1.0f, 0.0f);

		float angle = MathF.Atan2(onScreen.y, onScreen.x).RadianToDegree();

		// Never upside down: turn it half a turn when the line runs leftwards.
		if (angle > 90.0f)
			angle -= 180.0f;
		else if (angle <= -90.0f)
			angle += 180.0f;

		// Square to the text's own direction, the way that points up the screen.
		float radians = angle.DegreeToRadian();
		var normal = new Vector2(MathF.Sin(radians), -MathF.Cos(radians));

		var middle = new Vector2((from.x + to.x) * 0.5f, (from.y + to.y) * 0.5f) + normal * 12.0f;
		const float width = 120.0f;
		const float height = 20.0f;

		Gizmo.Draw.ScreenText(new TextRendering.Scope($"{length:0}", Color.Yellow, 12.0f), new Rect(middle.x, middle.y, width, height), angle, TextFlag.Center);
	}



	internal static void DrawDashed(Vector3 _From, Vector3 _To)
	{
		float length = Vector3.DistanceBetween(_From, _To);

		if (length < 0.01f)
			return;

		// Dashes keep a constant on-screen size.
		float dash = MathF.Max(10.0f, Gizmo.Camera.Position.Distance(_To) * 0.02f);
		Vector3 direction = (_To - _From) / length;

		for (float at = 0.0f; at < length; at += dash * 2.0f)
		{
			float end = MathF.Min(at + dash, length);
			Gizmo.Draw.Line(_From + direction * at, _From + direction * end);
		}
	}



	/// <summary>
	/// A flat arc round a point, from one heading to another the short way round.
	/// </summary>
	internal static void DrawArc(Vector3 _Centre, float _Radius, float _From, float _To)
	{
		float sweep = AngleDifference(_To, _From);
		int steps = Math.Max(2, (int)MathF.Ceiling(MathF.Abs(sweep) / 5.0f));
		Vector3 previous = _Centre + HeadingDirection(_From) * _Radius;

		for (int i = 1; i <= steps; i++)
		{
			Vector3 point = _Centre + HeadingDirection(_From + sweep * i / steps) * _Radius;

			Gizmo.Draw.Line(previous, point);
			previous = point;
		}
	}
}



file class RoadDrawToolWidget : ToolSidebarWidget
{
	private readonly RoadDrawTool m_Tool;



	public RoadDrawToolWidget(RoadDrawTool _Tool)
	{
		m_Tool = _Tool;

		AddTitle("Draw Road", "timeline");

		var serialized = _Tool.GetSerialized();
		var group = AddGroup("Road");

		SidebarRows.AddWide(group, serialized.GetProperty(nameof(RoadDrawTool.Profile)));
		SidebarRows.AddWide(group, serialized.GetProperty(nameof(RoadDrawTool.Bridge)));
		SidebarRows.AddWide(group, serialized.GetProperty(nameof(RoadDrawTool.Props)));
		SidebarRows.Add(group, serialized.GetProperty(nameof(RoadDrawTool.Width)));
		SidebarRows.Add(group, serialized.GetProperty(nameof(RoadDrawTool.Lanes)));
		SidebarRows.Add(group, serialized.GetProperty(nameof(RoadDrawTool.OneWay)));
		SidebarRows.Add(group, serialized.GetProperty(nameof(RoadDrawTool.SnapRadius)));
		SidebarRows.Add(group, serialized.GetProperty(nameof(RoadDrawTool.SharpCorners)));

		var snapping = AddGroup("Snapping");

		SidebarRows.Add(snapping, serialized.GetProperty(nameof(RoadDrawTool.AngleSnap)));
		SidebarRows.Add(snapping, serialized.GetProperty(nameof(RoadDrawTool.AngleStep)));

		AddShortcuts(
			("Place Node", "LMB"),
			("Continue From / Join Node", "LMB on node"),
			("Branch From / Merge Into Road", "LMB on road"),
			("Snap Angle + Align With Nodes", "Hold SHIFT"),
			("End Road", "RMB / ESC")
		);

		Layout.AddStretchCell();
	}



	[Shortcut("roadtool2.draw-end", "ESC", typeof(SceneViewWidget))]
	private void EndChain()
	{
		m_Tool.EndChain();
	}
}
