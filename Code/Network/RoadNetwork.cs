using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// The road network: a graph of nodes joined by segments, which is the single source of truth for every road in
/// it. All the meshes under this GameObject are derived from the graph and regenerated on demand, one chunk at a
/// time (see RoadNetwork.Rebuild.cs).
/// </summary>
[Icon("add_road")]
public sealed partial class RoadNetwork : Component, Component.ExecuteInEditor
{
	public const string ChunkTag = "road_chunk";

	/// <summary>
	/// Undo/redo and scene loads assign whole new lists here instead of mutating the existing ones, so the setters
	/// are the one place that reliably sees "the graph changed" and asks for a reconcile pass.
	/// </summary>
	[Property, Hide]
	public List<RoadNode> Nodes
	{
		get;
		set
		{
			field = value ?? new List<RoadNode>();
			MarkDirty();
		}
	} = [];

	[Property, Hide]
	public List<RoadSegment> Segments
	{
		get;
		set
		{
			field = value ?? new List<RoadSegment>();
			MarkDirty();
		}
	} = [];

	/// <summary>
	/// Shared counter for node and segment ids. Serialized so ids stay unique across undo and reloads.
	/// </summary>
	[Property, Hide] public int NextId { get; set; } = 1;

	/// <summary>
	/// The road type used by roads that have none of their own.
	/// </summary>
	[Property(Title = "Default Profile"), Group("Road")] public RoadProfile DefaultProfile { get; set { field = value; MarkDirty(); } }

	/// <summary>
	/// How far above the node positions the road surface sits. Nodes are placed on the ground, so without this the
	/// surface would z-fight with flat terrain.
	/// </summary>
	[Property(Title = "Surface Offset"), Group("Road"), Range(0.0f, 50.0f)] public float SurfaceOffset { get; set { field = value; MarkDirty(); } } = 1.0f;



	/// <summary>
	/// The road type a segment is built as: its own, else the network's default, else plain asphalt.
	/// </summary>
	public RoadProfile ProfileOf(RoadSegment _Segment)
	{
		return _Segment?.Profile ?? DefaultProfile ?? RoadProfile.Fallback;
	}

	[Property(Title = "Sample Spacing"), Group("Generation"), Range(10.0f, 200.0f)] public float SampleSpacing { get; set { field = value.Clamp(5.0f, 1000.0f); MarkDirty(); } } = 80.0f;
	[Property(Title = "Chunk Length"), Group("Generation"), Range(200.0f, 10000.0f)] public float ChunkLength { get; set { field = value.Clamp(100.0f, 100000.0f); MarkDirty(); } } = 3000.0f;

	/// <summary>
	/// How many chunks are (re)built per editor frame at least, however long they take.
	/// </summary>
	[Property(Title = "Chunks Per Frame"), Group("Generation"), Range(1, 32)] public int RebuildBudget { get; set; } = 4;

	/// <summary>
	/// How long, in milliseconds, each editor frame may keep (re)building chunks after the first
	/// <see cref="RebuildBudget"/>. Enough for the pieces round a dragged node to follow it every frame, while a
	/// long road catching up still leaves the editor responsive. 0 builds only <see cref="RebuildBudget"/>.
	/// </summary>
	[Property(Title = "Frame Time Budget (ms)"), Group("Generation"), Range(0.0f, 50.0f)] public float RebuildTimeBudget { get; set; } = 10.0f;

	/// <summary>
	/// How far beyond a road's border the terrain takes to blend back into the landscape when it is shaped to the
	/// roads (the Terrain tool's Conform, and Live Update).
	/// </summary>
	[Property(Title = "Falloff"), Group("Terrain"), Range(0.0f, 2000.0f)] public float TerrainFalloff { get; set { field = value.Clamp(0.0f, 100000.0f); } } = 300.0f;

	/// <summary>
	/// How far below the road surface the ground is kept, so it never shows through the asphalt.
	/// </summary>
	[Property(Title = "Sink"), Group("Terrain"), Range(0.0f, 50.0f)] public float TerrainSink { get; set { field = value.Clamp(0.0f, 1000.0f); } } = 5.0f;

	/// <summary>
	/// The terrain material painted along the roads. Give a country road's verge the same texture and the two merge,
	/// instead of the verge ending on a line against the landscape.
	/// </summary>
	[Property(Title = "Paint Material"), Group("Terrain")] public TerrainMaterial TerrainPaintMaterial { get; set; }

	/// <summary>
	/// How far past a road's border the material is painted at full strength.
	/// </summary>
	[Property(Title = "Paint Margin"), Group("Terrain"), Range(0.0f, 1000.0f)] public float TerrainPaintMargin { get; set { field = value.Clamp(0.0f, 100000.0f); } } = 100.0f;

	/// <summary>
	/// How far the material fades out beyond that, back into whatever the ground is painted with.
	/// </summary>
	[Property(Title = "Paint Fade"), Group("Terrain"), Range(0.0f, 2000.0f)] public float TerrainPaintFade { get; set { field = value.Clamp(0.0f, 100000.0f); } } = 200.0f;

	/// <summary>
	/// Keeps the terrain matching the roads while they are edited. Switched from the Terrain tool only: switching it
	/// on there lays every road in as part of the same undo step, which setting it from anywhere else would not.
	/// </summary>
	[Property, Hide] public bool TerrainLiveUpdate { get; set; }

	/// <summary>
	/// Draws every border piece's rows over the scene: arm chunks green, junction corner halves red and cyan,
	/// block corner patches yellow, caps magenta. The editor also writes the numbers behind every piece to
	/// Libraries/RoadTool2/debug/borders.txt whenever a piece is rebuilt.
	/// </summary>
	[Property(Title = "Debug Borders"), Group("Debug")] public bool DebugBorders { get; set { field = value; MarkDirty(); } }

	/// <summary>
	/// Bumped each time a border piece is built with its debug record, so the editor knows when to write it out.
	/// </summary>
	public int DebugRevision { get; private set; }



	/// <summary>
	/// Everything that wants the network asks the scene for it (<c>Scene.Get&lt;RoadNetwork&gt;()</c>), so a second
	/// one would be found by nobody in particular while both generated pieces over the same ground. The one that
	/// arrives second takes itself out again.
	/// </summary>
	protected override void OnAwake()
	{
		var existing = Scene.Get<RoadNetwork>();

		if (!existing.IsValid() || existing == this)
			return;

		Log.Warning($"Only one {nameof(RoadNetwork)} can exist in a scene: the one added to \"{GameObject.Name}\" has been removed. Use the one on \"{existing.GameObject.Name}\".");

		Destroy();
	}



	protected override void OnEnabled()
	{
		MarkDirty();
	}



	/// <summary>
	/// Undo/redo restore the graph by deserializing into this component; the list setters catch most of it, this
	/// catches the rest (and inspector edits).
	/// </summary>
	protected override void OnValidate()
	{
		MarkDirty();
	}



	protected override void OnUpdate()
	{
		// Only the editor scene generates; in play mode the pieces are whatever was saved.
		if (!Scene.IsEditor)
			return;

		Tick();
	}



	private readonly Dictionary<int, RoadNode> m_NodeIndex = new();
	private readonly Dictionary<int, RoadSegment> m_SegmentIndex = new();
	private readonly Dictionary<int, List<RoadSegment>> m_NodeSegments = new();
	private static readonly List<RoadSegment> s_NoSegments = new();

	private int m_GraphRevision;
	private int m_IndexRevision = -1;

	/// <summary>
	/// Bumped on every change to the graph, and to anything the roads are built from: a cheap way for the editor to
	/// tell that the roads may have changed since it last looked (Live Update).
	/// </summary>
	public int GraphRevision => m_GraphRevision;

	/// <summary>
	/// Says the graph's shape changed, so the lookups below are rebuilt before they are next used. Called at the
	/// mutation itself rather than at the end of the operation that does it, because the rest of that operation
	/// reads the graph back (a road removed, then its node's degree asked for).
	/// </summary>
	private void InvalidateIndex()
	{
		m_GraphRevision++;
	}



	/// <summary>
	/// Nodes and roads by id, and the roads at each node. Everything asks the graph these three questions
	/// constantly — the reconcile per node and per road, the editor's picking once a frame for every road — and
	/// answering them by scanning the lists makes all of that quadratic in the size of the network.
	/// </summary>
	private void EnsureIndex()
	{
		if (m_IndexRevision == m_GraphRevision)
			return;

		m_IndexRevision = m_GraphRevision;

		m_NodeIndex.Clear();
		m_SegmentIndex.Clear();

		// Fresh lists rather than cleared ones: a caller may still be walking the last set.
		m_NodeSegments.Clear();

		foreach (var node in Nodes)
		{
			if (node is not null)
				m_NodeIndex[node.Id] = node;
		}

		foreach (var segment in Segments)
		{
			if (segment is null)
				continue;

			m_SegmentIndex[segment.Id] = segment;

			Attach(segment.StartNodeId, segment);

			// A road looping back to its own node touches it once, as the scan it replaces counted it.
			if (segment.EndNodeId != segment.StartNodeId)
				Attach(segment.EndNodeId, segment);
		}
	}



	private void Attach(int _NodeId, RoadSegment _Segment)
	{
		if (!m_NodeSegments.TryGetValue(_NodeId, out var list))
		{
			list = new List<RoadSegment>();
			m_NodeSegments[_NodeId] = list;
		}

		list.Add(_Segment);
	}



	public RoadNode FindNode(int _Id)
	{
		EnsureIndex();

		return m_NodeIndex.GetValueOrDefault(_Id);
	}



	public RoadSegment FindSegment(int _Id)
	{
		EnsureIndex();

		return m_SegmentIndex.GetValueOrDefault(_Id);
	}



	public RoadSegment FindSegmentBetween(int _NodeA, int _NodeB)
	{
		if (_NodeA == _NodeB)
			return null;

		foreach (var segment in GetSegmentsAt(_NodeA))
		{
			if (segment.Touches(_NodeB))
				return segment;
		}

		return null;
	}



	/// <summary>
	/// The roads at a node. The list belongs to the network: treat it as read only, and copy it before doing
	/// anything that changes the graph while walking it.
	/// </summary>
	public IReadOnlyList<RoadSegment> GetSegmentsAt(int _NodeId)
	{
		EnsureIndex();

		return m_NodeSegments.TryGetValue(_NodeId, out var segments) ? segments : s_NoSegments;
	}



	public int GetDegree(int _NodeId)
	{
		return GetSegmentsAt(_NodeId).Count;
	}



	public RoadNode FindNearestNode(Vector3 _LocalPosition, float _MaxDistance)
	{
		RoadNode nearest = null;
		float nearestDistance = _MaxDistance;

		foreach (var node in Nodes)
		{
			float distance = Vector3.DistanceBetween(node.Position, _LocalPosition);

			if (distance > nearestDistance)
				continue;

			nearest = node;
			nearestDistance = distance;
		}

		return nearest;
	}



	public RoadNode AddNode(Vector3 _LocalPosition)
	{
		var node = new RoadNode { Id = NextId++, Position = _LocalPosition };

		Nodes.Add(node);
		MarkDirty();

		return node;
	}



	/// <summary>
	/// Joins two nodes with a road. Returns null when the join makes no sense (same node, unknown node, or a road
	/// already runs between them).
	/// </summary>
	public RoadSegment AddSegment(int _StartNodeId, int _EndNodeId, float _Width, RoadProfile _Profile = null, int _LaneCount = 2, int _ForwardLanes = 1, BridgeStyle _Bridge = null, RoadPropSet _Props = null)
	{
		if (_StartNodeId == _EndNodeId)
			return null;

		if (FindNode(_StartNodeId) is null || FindNode(_EndNodeId) is null)
			return null;

		if (FindSegmentBetween(_StartNodeId, _EndNodeId) is not null)
			return null;

		var start = FindNode(_StartNodeId);
		var end = FindNode(_EndNodeId);

		// Straight handles to begin with: that is what a node with hand-placed tangents keeps (the refresh below
		// leaves those nodes alone), and what a junction arm wants anyway.
		var segment = new RoadSegment
		{
			Id = NextId++,
			StartNodeId = _StartNodeId,
			EndNodeId = _EndNodeId,
			Width = _Width,
			Profile = _Profile,
			Bridge = _Bridge,
			Props = _Props,
			LaneCount = _LaneCount,
			ForwardLanes = _ForwardLanes,
			StartTangent = StraightHandle(start.Position, end.Position),
			EndTangent = StraightHandle(end.Position, start.Position)
		};

		Segments.Add(segment);
		InvalidateIndex();

		RefreshTangentsAround(_StartNodeId);
		RefreshTangentsAround(_EndNodeId);
		MarkDirty();

		return segment;
	}



	/// <summary>
	/// Inserts a node on a road without changing the road's shape: the segment keeps its id and becomes the first
	/// half, a new segment covers the rest. A split landing within <paramref name="_MinEndDistance"/> of an end
	/// returns that end node instead, so no sliver segments get made.
	/// </summary>
	public RoadNode SplitSegment(int _SegmentId, float _T, float _MinEndDistance = 25.0f)
	{
		var segment = FindSegment(_SegmentId);

		if (segment is null || !TryGetCurve(segment, out var curve))
			return null;

		var table = GetCurveTable(segment);
		float distance = _T * table.Length;

		if (distance <= _MinEndDistance)
			return FindNode(segment.StartNodeId);

		if (table.Length - distance <= _MinEndDistance)
			return FindNode(segment.EndNodeId);

		curve.Split(_T, out var first, out var second);

		// The split point's handles come straight from the subdivision, which is the only way the two halves keep
		// tracing the old road, so they are hand-placed from the node's point of view.
		var node = new RoadNode { Id = NextId++, Position = first.P3, TangentMode = AutoTangentMode.Disabled };
		Nodes.Add(node);

		int endNodeId = segment.EndNodeId;
		bool crossingAtEnd = segment.CrossingAtEnd;

		// The edges at the cut carry over to both halves, so the road keeps its shape there.
		var width = new RoadWidth(segment, table.Length);
		float leftAtCut = width.LeftExtraAt(distance);
		float rightAtCut = width.RightExtraAt(distance);
		float leftExtraEnd = segment.LeftExtraEnd;
		float rightExtraEnd = segment.RightExtraEnd;

		segment.EndNodeId = node.Id;
		segment.CrossingAtEnd = true;
		segment.LeftExtraEnd = leftAtCut;
		segment.RightExtraEnd = rightAtCut;
		segment.StartTangent = first.P1 - first.P0;
		segment.EndTangent = first.P2 - first.P3;

		Segments.Add(new RoadSegment
		{
			Id = NextId++,
			StartNodeId = node.Id,
			EndNodeId = endNodeId,
			Width = segment.Width,
			LeftExtraStart = leftAtCut,
			RightExtraStart = rightAtCut,
			LeftExtraEnd = leftExtraEnd,
			RightExtraEnd = rightExtraEnd,
			Ramp = segment.Ramp,
			Profile = segment.Profile,
			Bridge = segment.Bridge,
			Props = segment.Props,
			LaneCount = segment.LaneCount,
			ForwardLanes = segment.ForwardLanes,
			CrossingAtEnd = crossingAtEnd,
			ConformTerrain = segment.ConformTerrain,
			StartTangent = second.P1 - second.P0,
			EndTangent = second.P2 - second.P3
		});

		MarkDirty();

		return node;
	}



	/// <summary>
	/// Sets how a node's tangent handles are kept up. Coming off Full straightens every handle at the node, which is
	/// the "sharp corner" look, unless <paramref name="_Straighten"/> is off, which just freezes the handles as they are
	/// (so a road added next keeps its straight handle there without kinking the roads already through the node).
	/// Pitch Only keeps the directions the handles have and fits their pitch to the heights; Full hands the handles
	/// back to the network altogether.
	/// </summary>
	public void SetTangentMode(int _NodeId, AutoTangentMode _Mode, bool _Straighten = true)
	{
		var node = FindNode(_NodeId);

		if (node is null)
			return;

		// Handles that were the network's own (smooth through the node) are no shape anyone chose to keep; ones placed
		// by hand are, so only a node coming off Full is straightened.
		bool straighten = _Straighten && node.TangentMode == AutoTangentMode.Full && _Mode != AutoTangentMode.Full;

		node.TangentMode = _Mode;

		if (straighten)
		{
			foreach (var segment in GetSegmentsAt(_NodeId))
			{
				var other = FindNode(segment.OtherNodeId(_NodeId));

				if (other is not null)
					SetHandleInternal(segment, _NodeId, StraightHandle(node.Position, other.Position));
			}
		}

		RefreshTangentsAround(_NodeId);
		MarkDirty();
	}



	/// <summary>
	/// The Bezier handle a segment has at one of its nodes, pointing from the node into the segment.
	/// </summary>
	public static Vector3 GetHandle(RoadSegment _Segment, int _NodeId)
	{
		return _Segment.StartNodeId == _NodeId ? _Segment.StartTangent : _Segment.EndTangent;
	}



	/// <summary>
	/// Places one handle by hand. With <paramref name="_Mirror"/>, a pass-through node keeps its other handle
	/// opposite (own length kept) so the road stays smooth through it. A node on Full stops working its handles out;
	/// one on Pitch Only keeps fitting their pitch, which is all it ever does, to the directions just given.
	/// </summary>
	public void SetHandle(int _SegmentId, int _NodeId, Vector3 _Handle, bool _Mirror)
	{
		var segment = FindSegment(_SegmentId);
		var node = FindNode(_NodeId);

		if (segment is null || node is null || !segment.Touches(_NodeId))
			return;

		if (node.TangentMode == AutoTangentMode.Full)
			node.TangentMode = AutoTangentMode.Disabled;

		SetHandleInternal(segment, _NodeId, _Handle);

		if (_Mirror && !_Handle.IsNearZeroLength)
		{
			var attached = GetSegmentsAt(_NodeId).ToList();

			if (attached.Count == 2)
			{
				var other = attached[0] == segment ? attached[1] : attached[0];
				float otherLength = GetHandle(other, _NodeId).Length;

				SetHandleInternal(other, _NodeId, -_Handle.Normal * otherLength);
			}
		}

		RefreshTangentsAt(_NodeId);
		MarkDirty();
	}



	private static void SetHandleInternal(RoadSegment _Segment, int _NodeId, Vector3 _Handle)
	{
		if (_Segment.StartNodeId == _NodeId)
			_Segment.StartTangent = _Handle;
		else
			_Segment.EndTangent = _Handle;
	}



	public void MoveNode(int _NodeId, Vector3 _LocalPosition)
	{
		var node = FindNode(_NodeId);

		if (node is null)
			return;

		// A drag that is holding still would otherwise rebuild the chunks every frame over float noise.
		if (Vector3.DistanceBetween(node.Position, _LocalPosition) < 0.01f)
			return;

		node.Position = _LocalPosition;

		RefreshTangentsAround(_NodeId);
		MarkDirty();
	}



	/// <summary>
	/// Sets a road's lanes: how many, and how many of them run start to end. Only the road's painted lines are
	/// rebuilt.
	/// </summary>
	public void SetLanes(int _SegmentId, int _LaneCount, int _ForwardLanes)
	{
		var segment = FindSegment(_SegmentId);

		if (segment is null)
			return;

		segment.LaneCount = _LaneCount;
		segment.ForwardLanes = _ForwardLanes.Clamp(0, segment.LaneCount);
		MarkDirty();
	}



	/// <summary>
	/// Removes a road. A node left with nothing attached goes with it, the others get their smoothing refreshed.
	/// </summary>
	public bool RemoveSegment(int _SegmentId)
	{
		var segment = FindSegment(_SegmentId);

		if (segment is null)
			return false;

		Segments.Remove(segment);
		InvalidateIndex();

		foreach (int nodeId in new[] { segment.StartNodeId, segment.EndNodeId })
		{
			if (GetDegree(nodeId) == 0)
			{
				Nodes.RemoveAll(node => node.Id == nodeId);
				InvalidateIndex();
			}
			else
			{
				RefreshTangentsAround(nodeId);
			}
		}

		MarkDirty();

		return true;
	}



	public bool RemoveNode(int _NodeId)
	{
		if (FindNode(_NodeId) is null)
			return false;

		foreach (var segment in GetSegmentsAt(_NodeId).ToList())
			RemoveSegment(segment.Id);

		// RemoveSegment already dropped it once it had no roads left; this covers a node that never had any.
		Nodes.RemoveAll(node => node.Id == _NodeId);
		MarkDirty();

		return true;
	}



	/// <summary>
	/// Takes a pass-through node out and joins its two roads into one, so the road carries on without it. The
	/// merged road keeps the handles the two roads had at their far ends, stretched to the full length (which
	/// undoes a split exactly on a straight road and closely on a curved one), and the longer road's width and
	/// type. Returns the merged road, or null when the node is not a plain pass-through (a junction, a road end,
	/// a loop, or two roads whose far ends are already joined), in which case nothing changes.
	/// </summary>
	public RoadSegment DissolveNode(int _NodeId)
	{
		var node = FindNode(_NodeId);

		if (node is null)
			return null;

		var attached = GetSegmentsAt(_NodeId).ToList();

		if (attached.Count != 2)
			return null;

		var first = attached[0];
		var second = attached[1];
		int startId = first.OtherNodeId(_NodeId);
		int endId = second.OtherNodeId(_NodeId);

		if (startId == endId || FindSegmentBetween(startId, endId) is not null)
			return null;

		float firstLength = GetCurveTable(first)?.Length ?? 0.0f;
		float secondLength = GetCurveTable(second)?.Length ?? 0.0f;
		float total = firstLength + secondLength;
		var main = secondLength > firstLength ? second : first;

		// The merged road runs startId to endId; a road that ran the other way has its lane directions mirrored.
		bool mainSameWay = main == first ? first.StartNodeId == startId : second.StartNodeId == _NodeId;

		// The far ends keep their edges (a road that ran the other way has its sides swapped); the base width is
		// the main road's, so the extras are worked back from the edges themselves.
		EdgesAtNode(first, startId, out float startLeft, out float startRight);
		EdgesAtNode(second, endId, out float endLeft, out float endRight);
		bool firstSameWay = first.StartNodeId == startId;
		bool secondSameWay = second.EndNodeId == endId;

		var merged = new RoadSegment
		{
			Id = NextId++,
			StartNodeId = startId,
			EndNodeId = endId,
			Width = main.Width,
			LeftExtraStart = (firstSameWay ? startLeft : startRight) - main.Width * 0.5f,
			RightExtraStart = (firstSameWay ? startRight : startLeft) - main.Width * 0.5f,
			LeftExtraEnd = (secondSameWay ? endLeft : endRight) - main.Width * 0.5f,
			RightExtraEnd = (secondSameWay ? endRight : endLeft) - main.Width * 0.5f,
			Ramp = first.Ramp || second.Ramp,
			Profile = main.Profile,
			Bridge = main.Bridge,
			Props = main.Props,
			LaneCount = main.LaneCount,
			ForwardLanes = mainSameWay ? main.ForwardLanes : main.LaneCount - main.ForwardLanes,
			CrossingAtStart = first.CrossingAt(startId),
			CrossingAtEnd = second.CrossingAt(endId),
			ConformTerrain = main.ConformTerrain,
			StartTangent = GetHandle(first, startId) * (firstLength > 0.0f ? total / firstLength : 1.0f),
			EndTangent = GetHandle(second, endId) * (secondLength > 0.0f ? total / secondLength : 1.0f)
		};

		Segments.Remove(first);
		Segments.Remove(second);
		Nodes.Remove(node);
		Segments.Add(merged);
		InvalidateIndex();

		RefreshTangentsAround(startId);
		RefreshTangentsAround(endId);
		MarkDirty();

		return merged;
	}



	public bool TryGetCurve(RoadSegment _Segment, out RoadCurve _Curve)
	{
		var start = FindNode(_Segment.StartNodeId);
		var end = FindNode(_Segment.EndNodeId);

		if (start is null || end is null)
		{
			_Curve = default;
			return false;
		}

		Vector3 p0 = start.Position;
		Vector3 p3 = end.Position;
		Vector3 startHandle = _Segment.StartTangent;
		Vector3 endHandle = _Segment.EndTangent;

		// A road opening into a parking lot leaves its edge square on, so the two surfaces butt together.
		if (TryLotOutward(_Segment.StartNodeId, out Vector3 startOutward))
			startHandle = startOutward * startHandle.Length;

		if (TryLotOutward(_Segment.EndNodeId, out Vector3 endOutward))
			endHandle = endOutward * endHandle.Length;

		// A ramp's end at a junction is moved to the side of the road it runs off, and turned to run along it.
		if (_Segment.Ramp)
		{
			if (TryRampEnd(_Segment, _Segment.StartNodeId, out Vector3 shift, out Vector3 direction))
			{
				p0 += shift;
				startHandle = direction * startHandle.Length;
			}

			if (TryRampEnd(_Segment, _Segment.EndNodeId, out shift, out direction))
			{
				p3 += shift;
				endHandle = direction * endHandle.Length;
			}
		}

		_Curve = new RoadCurve(p0, p0 + startHandle, p3 + endHandle, p3);
		return true;
	}



	/// <summary>
	/// The edges of a segment along its length: its width, and the extras its sides carry at each end.
	/// </summary>
	public RoadWidth WidthOf(RoadSegment _Segment)
	{
		var table = GetCurveTable(_Segment);

		return new RoadWidth(_Segment, table?.Length ?? 1.0f);
	}



	/// <summary>
	/// Where a ramp's end at a junction goes. The road it runs off is the pair of other arms most nearly in line
	/// with each other (the road running through the node); the ramp's centreline is shifted so that its outer
	/// edge meets the widest edge that road has on the ramp's side, and its handle turned along the road, in the
	/// sense the ramp heads. False at a node with no road running through (a road end, a pass-through node, or
	/// three roads all meeting at angles).
	/// </summary>
	private bool TryRampEnd(RoadSegment _Segment, int _NodeId, out Vector3 _Shift, out Vector3 _Direction)
	{
		_Shift = Vector3.Zero;
		_Direction = Vector3.Zero;

		var node = FindNode(_NodeId);
		var far = FindNode(_Segment.OtherNodeId(_NodeId));

		if (node is null || far is null)
			return false;

		Vector3 chord = (far.Position - node.Position).WithZ(0.0f);

		if (chord.IsNearZeroLength)
			return false;

		chord = chord.Normal;

		var others = GetSegmentsAt(_NodeId).Where(candidate => candidate.Id != _Segment.Id && !candidate.Ramp).ToList();

		if (others.Count < 2)
			return false;

		// The through road: the two arms leaving the node most nearly opposite each other, as long as they are
		// within 60 degrees of straight.
		RoadSegment throughA = null;
		RoadSegment throughB = null;
		float straightest = 0.5f;

		for (int i = 0; i < others.Count; i++)
		{
			for (int j = i + 1; j < others.Count; j++)
			{
				float straightness = -Vector3.Dot(LeavingDirection(others[i], _NodeId), LeavingDirection(others[j], _NodeId));

				if (straightness > straightest)
				{
					straightest = straightness;
					throughA = others[i];
					throughB = others[j];
				}
			}
		}

		if (throughA is null)
			return false;

		// Along the through road in the sense the ramp heads: the arm of the pair the ramp's far end lies towards.
		Vector3 alongA = LeavingDirection(throughA, _NodeId);
		Vector3 alongB = LeavingDirection(throughB, _NodeId);
		Vector3 along = Vector3.Dot(chord, alongA) >= Vector3.Dot(chord, alongB) ? alongA : -alongB;
		Vector3 left = Vector3.Cross(Vector3.Up, along).Normal;
		float side = Vector3.Dot(chord, left) >= 0.0f ? 1.0f : -1.0f;
		Vector3 sideDirection = left * side;

		// The widest edge on the ramp's side of the two through arms.
		float edge = 0.0f;

		foreach (var candidate in new[] { throughA, throughB })
		{
			float alignment = Vector3.Dot(LeavingDirection(candidate, _NodeId), along);

			// Looking out along an arm that runs the other way, the ramp's side is the arm's other side; and an arm
			// ending at the node is looked out along backwards, which swaps its own sides again.
			bool onArmLeft = side > 0.0f == alignment > 0.0f;
			bool segmentLeft = candidate.StartNodeId == _NodeId ? onArmLeft : !onArmLeft;

			edge = MathF.Max(edge, EdgeAtNode(candidate, _NodeId, segmentLeft));
		}

		// The ramp's own outer edge: the one on its side looking along its own way of travel near the node.
		Vector3 travel = _Segment.StartNodeId == _NodeId ? chord : -chord;
		bool travelsAlong = Vector3.Dot(travel, along) >= 0.0f;
		bool ownLeft = side > 0.0f == travelsAlong;
		float own = EdgeAtNode(_Segment, _NodeId, ownLeft);

		// Nothing wider than the ramp itself to move onto (the through road has no extra lane there): the ramp
		// would only be parked on the road's centreline, running along it, so it is left as an ordinary arm.
		if (edge - own < 1.0f)
			return false;

		_Shift = sideDirection * (edge - own);
		_Direction = Vector3.Dot(chord, along) >= 0.0f ? along : -along;

		return true;
	}



	/// <summary>
	/// The horizontal direction a segment leaves a node in: its handle there, or straight at its far node when
	/// the handle is nothing.
	/// </summary>
	private Vector3 LeavingDirection(RoadSegment _Segment, int _NodeId)
	{
		Vector3 handle = GetHandle(_Segment, _NodeId).WithZ(0.0f);

		if (!handle.IsNearZeroLength)
			return handle.Normal;

		var node = FindNode(_NodeId);
		var far = FindNode(_Segment.OtherNodeId(_NodeId));

		if (node is null || far is null)
			return Vector3.Forward;

		Vector3 chord = (far.Position - node.Position).WithZ(0.0f);

		return chord.IsNearZeroLength ? Vector3.Forward : chord.Normal;
	}



	/// <summary>
	/// How far from its centreline a segment's edge sits at one of its nodes; the side is the segment's own (left
	/// looking from its start to its end).
	/// </summary>
	private static float EdgeAtNode(RoadSegment _Segment, int _NodeId, bool _Left)
	{
		bool atStart = _Segment.StartNodeId == _NodeId;
		float extra = _Left ? (atStart ? _Segment.LeftExtraStart : _Segment.LeftExtraEnd) : (atStart ? _Segment.RightExtraStart : _Segment.RightExtraEnd);

		return _Segment.Width * 0.5f + extra;
	}



	private static void EdgesAtNode(RoadSegment _Segment, int _NodeId, out float _Left, out float _Right)
	{
		_Left = EdgeAtNode(_Segment, _NodeId, true);
		_Right = EdgeAtNode(_Segment, _NodeId, false);
	}



	/// <summary>
	/// Whether two roads meeting at a pass-through node have the same edges there, so one carries straight on
	/// from the other: looking out of the node along each, one's left is the other's right.
	/// </summary>
	private static bool SameEdgesAt(RoadSegment _A, RoadSegment _B, int _NodeId)
	{
		float outLeftA = EdgeAtNode(_A, _NodeId, _A.StartNodeId == _NodeId);
		float outRightA = EdgeAtNode(_A, _NodeId, _A.StartNodeId != _NodeId);
		float outLeftB = EdgeAtNode(_B, _NodeId, _B.StartNodeId == _NodeId);
		float outRightB = EdgeAtNode(_B, _NodeId, _B.StartNodeId != _NodeId);

		return MathF.Abs(outLeftA - outRightB) < 0.01f && MathF.Abs(outRightA - outLeftB) < 0.01f;
	}



	private readonly Dictionary<int, RoadCurveTable> m_CurveTables = new();

	/// <summary>
	/// The arc-length table of a segment, rebuilt only when its curve actually changed. Picking asks for every
	/// segment's table every frame, so this has to be cheap on the no-change path.
	/// </summary>
	public RoadCurveTable GetCurveTable(RoadSegment _Segment)
	{
		if (!TryGetCurve(_Segment, out var curve))
			return null;

		if (m_CurveTables.TryGetValue(_Segment.Id, out var table) && table.Curve.Equals(curve))
			return table;

		table = RoadCurveTable.Create(curve);
		m_CurveTables[_Segment.Id] = table;

		return table;
	}



	private void PruneCurveTables()
	{
		var live = new HashSet<int>(Segments.Select(segment => segment.Id));
		var stale = m_CurveTables.Keys.Where(id => !live.Contains(id)).ToList();

		foreach (int id in stale)
			m_CurveTables.Remove(id);
	}
}
