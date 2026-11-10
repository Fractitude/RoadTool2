using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;

namespace RedSnail.RoadTool2.Editor;

/// <summary>
/// Terrain and roads, both ways: shape the terrain to the roads (the ground is cut and filled to meet the road
/// surface and its borders, blending back into the landscape over a falloff), or drop the road nodes onto the
/// ground. Both are explicit and undoable, unless Live Update is on, when the terrain follows the roads by itself
/// (see <see cref="RoadTerrainLive"/>). What the roads shape and paint goes through each terrain's road layer (see
/// <see cref="RoadTerrainLayers"/>), so it can be done again as often as the roads change, and taken back out. The
/// settings live on the network, so they are saved with the scene and Live Update works from the same ones.
/// </summary>
[Title("Terrain")]
[Icon("landscape")]
[Group("1")]
[Order(3)]
public class RoadTerrainTool : EditorTool
{
	public RoadNetwork Network => Root?.Network;

	private RoadNetworkTool Root => Manager?.CurrentTool as RoadNetworkTool;

	/// <summary>
	/// A short report of the last operation, for the sidebar.
	/// </summary>
	public string LastResult { get; private set; } = "";

	public int ResultVersion { get; private set; }



	public override Widget CreateToolSidebar()
	{
		return new RoadTerrainToolWidget(this);
	}



	public override void OnUpdate()
	{
		var network = Network;

		if (!network.IsValid())
			return;

		// Show how far the terrain will be touched: the outer edge of the falloff round every road.
		using (Gizmo.Scope("roadtool2_terrain"))
		{
			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.LineThickness = 1.0f;
			Gizmo.Draw.Color = Color.Green.WithAlpha(0.35f);

			foreach (var segment in network.Segments)
			{
				if (segment.Bridge is not null || !segment.ConformTerrain || !network.TryGetCurve(segment, out var curve))
					continue;

				float reach = network.WidthOf(segment).MaxHalf * 2.0f + 2.0f * (RoadTerrainConformer.BorderWidth(network.ProfileOf(segment)) + network.TerrainFalloff);
				RoadGizmos.DrawRoadOutline(curve, reach, network.WorldTransform);
			}
		}
	}



	/// <summary>
	/// Where the road layer on each terrain stands, for the sidebar.
	/// </summary>
	public string LayerStatus => RoadTerrainLayers.Describe(RoadTerrainLayers.TerrainsIn(Scene));



	/// <summary>
	/// Takes the terrain as it is now as the ground the roads are laid over, once the user has confirmed it.
	/// </summary>
	public void StartRoadLayer()
	{
		var terrains = RoadTerrainLayers.TerrainsIn(Scene);

		if (terrains.Count == 0)
		{
			Report("No terrain in the scene.");
			return;
		}

		// Live Update would lay the roads straight back over the new ground, outside the undo step that started it.
		if (Network is { TerrainLiveUpdate: true })
		{
			Report("Switch Live Update off before starting the road layer.");
			return;
		}

		string question = RoadTerrainLayers.HeldBy(terrains) > 0
			? "The road layer already remembers the ground under the roads. Starting it again takes the terrain as it is now, roads and all, as the new ground, and what the roads did so far can no longer be taken out. Press Remove Road Layer first to take it out.\n\nStart the road layer again anyway?"
			: "Are you sure you want to take a snapshot of the current terrain? It becomes the baseline the road layer remembers, under everything the roads shape and paint from now on.";

		Dialog.AskConfirm(() =>
		{
			var valid = terrains.Where(terrain => terrain.IsValid() && terrain.Storage is not null).ToList();

			RoadTerrainLayers.Start(valid);
			Report(valid.Count == 1 ? "Road layer started: the terrain as it is now is the ground under the roads." : $"Road layer started on {valid.Count} terrains: each as it is now is the ground under the roads.");
		}, question, "Start Road Layer", "Start", "Cancel");
	}



	/// <summary>
	/// Gives the ground back everywhere the roads shaped or painted it, once the user has confirmed it.
	/// </summary>
	public void RemoveRoadLayer()
	{
		var terrains = RoadTerrainLayers.TerrainsIn(Scene);

		if (RoadTerrainLayers.HeldBy(terrains) == 0)
		{
			Report("The road layer holds nothing to take out.");
			return;
		}

		Dialog.AskConfirm(() =>
		{
			var network = Network;
			bool live = network is { TerrainLiveUpdate: true };
			var undo = new RoadTerrainUndo();
			int changed = RoadTerrainLayers.Remove(terrains.Where(terrain => terrain.IsValid() && terrain.Storage is not null).ToList(), network, undo, out string problem);

			undo.Push("Remove Road Layer");
			Report($"Roads taken out of the terrain: {changed:N0} samples are back on their ground.{(live ? " Live Update is off, as the terrain no longer follows the roads." : "")}", problem);
		}, "Take the roads back out of the terrain? Every sample they shaped or painted gets its ground back, keeping your own sculpting and painting there. The roads themselves stay where they are.", "Remove Road Layer", "Remove", "Cancel");
	}



	/// <summary>
	/// Switches Live Update. Switching it on lays every road into the terrain there and then, in the same undo step,
	/// so the terrain starts out matching the roads, and taking the step back takes the terrain back as it was.
	/// </summary>
	public void SetLiveUpdate(bool _On)
	{
		var network = Network;

		if (!network.IsValid() || network.TerrainLiveUpdate == _On)
			return;

		var terrains = RoadTerrainLayers.TerrainsIn(Scene);

		// Live Update works through the road layer, and the layer can't be started while it is on.
		if (_On && terrains.All(terrain => RoadTerrainLayers.LayerOf(terrain, out _) is null))
		{
			Report(terrains.Count == 0 ? "No terrain in the scene." : "Start the road layer first: Live Update lays the roads in through it.");
			return;
		}

		var undo = new RoadTerrainUndo();

		network.TerrainLiveUpdate = _On;

		undo.Add(() =>
		{
			if (network.IsValid())
				network.TerrainLiveUpdate = !_On;
		}, () =>
		{
			if (network.IsValid())
				network.TerrainLiveUpdate = _On;
		});

		int changed = 0;
		string problem = null;

		if (_On)
			changed = LayAll(network, terrains, undo, out problem);

		undo.Push(_On ? "Live Update On" : "Live Update Off");

		// The terrain matches the roads now: Live Update picks up from here instead of laying them all in again.
		RoadTerrainLive.Forget();

		if (!_On)
			Report("Live Update off: the terrain stays as it is when the roads change, until Conform and Paint bring it back in line.");
		else if (changed > 0)
			Report($"Live Update on: the terrain was brought in line with the roads ({changed:N0} samples changed) and follows them from now on.", problem);
		else
			Report("Live Update on: the terrain follows the roads from now on.", problem);
	}



	public void ConformTerrain()
	{
		var network = Network;

		if (!network.IsValid())
			return;

		var terrains = RoadTerrainLayers.TerrainsIn(Scene);

		if (terrains.Count == 0)
		{
			Report("No terrain in the scene.");
			return;
		}

		var undo = new RoadTerrainUndo();
		var shapers = RoadTerrainConformer.Collect(network);
		int changed = 0;
		int held = 0;
		bool laid = false;
		string problem = null;

		foreach (var terrain in terrains)
		{
			var layer = RoadTerrainLayers.LayerOf(terrain, out string reason);

			if (layer is not null)
			{
				changed += RoadTerrainConformer.Conform(terrain, layer, network, shapers, null, undo, out _, out int samples, out reason);
				held += samples;
				laid |= reason is null;
			}

			problem ??= reason;
		}

		undo.Push("Conform Terrain To Roads");

		if (!laid)
			Report(problem ?? "No road lies over a terrain.");
		else if (changed > 0)
			Report($"Terrain shaped under the roads: {changed:N0} height samples changed, {held:N0} held by the road layer.", problem);
		else
			Report(held > 0 ? $"The terrain already matches the roads ({held:N0} height samples held by the road layer)." : "No road lies over a terrain.", problem);
	}



	public void PaintTerrain()
	{
		var network = Network;

		if (!network.IsValid())
			return;

		if (network.TerrainPaintMaterial is null)
		{
			Report("Pick a terrain material to paint with first.");
			return;
		}

		var terrains = RoadTerrainLayers.TerrainsIn(Scene);

		if (terrains.Count == 0)
		{
			Report("No terrain in the scene.");
			return;
		}

		var undo = new RoadTerrainUndo();
		var strokes = RoadTerrainPainter.Collect(network);
		int changed = 0;
		int held = 0;
		bool laid = false;
		string problem = null;

		foreach (var terrain in terrains)
		{
			var layer = RoadTerrainLayers.LayerOf(terrain, out string reason);

			if (layer is not null)
			{
				changed += RoadTerrainPainter.Paint(terrain, layer, network, strokes, null, undo, out _, out int texels, out reason);
				held += texels;
				laid |= reason is null;
			}

			problem ??= reason;
		}

		undo.Push("Paint Terrain Along Roads");

		if (!laid)
			Report(problem ?? "No road lies over a terrain.");
		else if (changed > 0)
			Report($"Terrain painted along the roads: {changed:N0} texels changed, {held:N0} held by the road layer.", problem);
		else
			Report(held > 0 ? $"The terrain is already painted along the roads ({held:N0} texels held by the road layer)." : "No road lies over a terrain.", problem);
	}



	/// <summary>
	/// Shapes every terrain that has a road layer, and paints it when a paint material is set, into one undo step.
	/// Returns how many samples changed.
	/// </summary>
	private static int LayAll(RoadNetwork _Network, IReadOnlyList<Terrain> _Terrains, RoadTerrainUndo _Undo, out string _Problem)
	{
		_Problem = null;

		var shapers = RoadTerrainConformer.Collect(_Network);
		var strokes = _Network.TerrainPaintMaterial is null ? null : RoadTerrainPainter.Collect(_Network);
		int changed = 0;

		foreach (var terrain in _Terrains)
		{
			var layer = RoadTerrainLayers.LayerOf(terrain, out string reason);

			if (layer is not null)
			{
				changed += RoadTerrainConformer.Conform(terrain, layer, _Network, shapers, null, _Undo, out _, out _, out reason);

				if (strokes is not null)
				{
					changed += RoadTerrainPainter.Paint(terrain, layer, _Network, strokes, null, _Undo, out _, out _, out string paint);
					reason ??= paint;
				}
			}

			_Problem ??= reason;
		}

		return changed;
	}



	/// <summary>
	/// Drops every node onto whatever is under it (roads skipped), keeping the roads smooth between nodes.
	/// </summary>
	public void SnapNodesToGround()
	{
		var network = Network;

		if (!network.IsValid())
			return;

		int moved = 0;

		using (SceneEditorSession.Active.UndoScope("Snap Nodes To Ground").WithComponentChanges(network).Push())
		{
			foreach (var node in network.Nodes.ToList())
			{
				// A node with only bridges at it is up in the air on purpose; a bridge's end still lands.
				var roads = network.GetSegmentsAt(node.Id).ToList();

				if (roads.Count > 0 && roads.All(segment => segment.Bridge is not null))
					continue;

				Vector3 world = network.WorldTransform.PointToWorld(node.Position);

				if (!TryGroundHeight(world, out float height) || MathF.Abs(height - world.z) < 0.01f)
					continue;

				network.MoveNode(node.Id, network.WorldTransform.PointToLocal(world.WithZ(height)));
				moved++;
			}
		}

		Report(moved > 0 ? $"{moved} node{(moved == 1 ? "" : "s")} dropped onto the ground." : "Every node already sits on the ground.");
	}



	private bool TryGroundHeight(Vector3 _World, out float _Height)
	{
		const float reach = 20000.0f;

		var ray = new Ray(_World.WithZ(_World.z + reach * 0.5f), Vector3.Down);
		var physics = Scene.Trace.Ray(ray, reach).WithoutTags(RoadNetwork.ChunkTag).Run();

		if (physics.Hit)
		{
			// The ground the roads are laid over, not the terrain as they shaped it, or the nodes would settle into
			// their own roads' cuts.
			_Height = RoadTerrainLayers.OntoGround(physics, ray).z;
			return true;
		}

		var meshes = Scene.Trace.Ray(ray, reach).UseRenderMeshes(true, EditorPreferences.BackfaceSelection).WithoutTags("hidden", RoadNetwork.ChunkTag).UsePhysicsWorld(false).Run();

		_Height = meshes.Hit ? meshes.EndPosition.z : 0.0f;

		return meshes.Hit;
	}



	/// <summary>
	/// Shows the outcome of an operation, followed by what kept part of it from being done, if anything did.
	/// </summary>
	private void Report(string _Message, string _Problem = null)
	{
		LastResult = _Problem is null ? _Message : $"{_Message} {_Problem}";
		ResultVersion++;
		Log.Info($"Road Tool 2: {LastResult}");
	}
}



/// <summary>
/// Shapes a terrain's heightmap to a road network. Every height sample within reach of a road takes the height
/// of the nearest road there: below the surface under the road and its border, the border's own outer height at
/// its edge, and a smooth blend back to the untouched terrain across the falloff. Where roads meet, the nearest
/// one wins, which is what the junction geometry does too. A road set to leave the ground alone shapes nothing,
/// but the others' shaping is kept from coming up through it.
/// </summary>
internal static class RoadTerrainConformer
{
	/// <summary>
	/// What the roads within reach of one height sample ask of it: the height the nearest road wants the ground
	/// at (under the road, or at its border's edge), and how much of the untouched ground is left once every
	/// road's pull on the sample is taken together. A sample between two roads is pulled by both, so the ground
	/// between them stays level with them instead of mounding up as soon as it leaves either edge.
	/// </summary>
	private readonly struct Entry
	{
		public readonly float Distance;
		public readonly float Anchor;
		public readonly float Remaining;

		public Entry(float _Distance, float _Anchor, float _Remaining)
		{
			Distance = _Distance;
			Anchor = _Anchor;
			Remaining = _Remaining;
		}
	}

	/// <summary>
	/// One side of a piece of road: how far out from the centreline its edge sits, the width of its border there,
	/// and how much of the border's relief is in force.
	/// </summary>
	internal readonly record struct Side(float Edge, float Border, float Relief);

	/// <summary>
	/// One straight piece of a road, between two samples of its curve (world space), with each side as the road has
	/// it at the piece's middle, and how much further down than the road's top its surface reaches there (the bottom
	/// of its trails, as deep as they are cut at the piece's middle). On a bridge's deck, each side's edge is how far
	/// out the deck reaches.
	/// </summary>
	internal readonly record struct Piece(Vector3 From, Vector3 To, Side Left, Side Right, float Sunk = 0.0f)
	{
		public void AddToHash(ref StableHash _Hash)
		{
			_Hash.Add(From);
			_Hash.Add(To);
			_Hash.Add(Left.Edge);
			_Hash.Add(Left.Border);
			_Hash.Add(Left.Relief);
			_Hash.Add(Right.Edge);
			_Hash.Add(Right.Border);
			_Hash.Add(Right.Relief);
			_Hash.Add(Sunk);
		}
	}

	/// <summary>
	/// One of the things that shape the ground, in world space: a road (its pieces), a bridge's deck (the ground kept
	/// under it rather than shaped to it), or a junction, parking lot or rounded dead end (a surface, and the
	/// pavement round it when it has one).
	/// </summary>
	internal sealed class Shaper
	{
		public RoadProfile Profile { get; init; }
		public bool Deck { get; init; }

		/// <summary>
		/// A road set to leave the ground alone (and a junction or rounded end all of whose roads are): it shapes
		/// nothing, but the other roads' shaping must not bring the ground up through it.
		/// </summary>
		public bool Guard { get; init; }

		public float Reach { get; init; }
		public Piece[] Pieces { get; init; }
		public Vector3 Centre { get; init; }
		public Vector3[] Outline { get; init; }
		public float PavementReach { get; init; }
		public float PavementTop { get; init; }

		/// <summary>
		/// How high and how wide the ground banks up round an area, at each point of its outline (x the height, y the
		/// width), taken along the outline's edges between them: a banked road's rounded end, all round; a junction,
		/// along the stretches its banked roads own.
		/// </summary>
		public Vector2[] Banks { get; init; }

		public RoadTerrainFootprint Footprint { get; init; }
	}



	/// <summary>
	/// How far out from the road edge the terrain is shaped to the road before the falloff starts: the pavement
	/// of a sidewalk, the shoulder of a country road (its verge is meant to run under the ground, not sit on it), the
	/// bank beside a banked road.
	/// </summary>
	public static float BorderWidth(RoadProfile _Profile)
	{
		if (_Profile.HasBanks)
			return _Profile.BankWidth;

		return _Profile.Border switch
		{
			RoadBorderStyle.Sidewalk => _Profile.BorderWidth,
			RoadBorderStyle.CountrySide => _Profile.BorderWidth,
			_ => 0.0f
		};
	}



	/// <summary>
	/// Everything in a network that shapes the ground, with the network's terrain settings folded into each one's
	/// signature, so a change of falloff or sink touches all of them.
	/// </summary>
	public static List<Shaper> Collect(RoadNetwork _Network)
	{
		float falloff = _Network.TerrainFalloff;
		float sink = _Network.TerrainSink;
		float surfaceOffset = _Network.SurfaceOffset;
		var transform = _Network.WorldTransform;
		var shapers = new List<Shaper>();
		var trails = _Network.GetTrailFades();

		foreach (var segment in _Network.Segments)
		{
			var table = _Network.GetCurveTable(segment);

			if (table is null || table.SampleCount < 2)
				continue;

			var profile = _Network.ProfileOf(segment);
			var taper = _Network.GetBorderTaper(segment);
			var width = new RoadWidth(segment, table.Length);
			float border = BorderWidth(profile);

			// A bank runs the road's whole length, so its skirt is covered everywhere; it carries on round a rounded end
			// and along the road's share of a junction's corners (see GetGroundAreas).
			bool banked = profile.HasBanks;

			// The ground is kept under the bottom of the road's trails only where they are cut: they rise out of the
			// road towards a junction or a rounded end, and the ground there can come as close as under any road.
			var fade = trails.GetValueOrDefault(segment.Id);

			// A road set to leave the ground alone (one on a dam, a quay, a model of its own) shapes nothing, but it
			// still guards its surface and its border: a road next to it shapes the ground out past its own end and
			// sides, and would otherwise bring it up through this one.
			bool guard = !segment.ConformTerrain;

			// A bridge spans the terrain: the ground under it is not shaped to it, only kept under its deck, which
			// is what stops it showing through the deck's ends where the bridge meets the ground.
			bool deck = segment.Bridge is not null;
			float overhang = deck ? profile.FlatBorderWidth + segment.Bridge.SideStripWidth + segment.Bridge.DeckOverhang : 0.0f;
			float reach = deck ? width.MaxHalf + overhang : width.MaxHalf + border + (guard ? 0.0f : falloff);
			var pieces = new Piece[table.SampleCount - 1];
			Vector3 previous = transform.PointToWorld(table.SamplePosition(0));

			for (int i = 1; i < table.SampleCount; i++)
			{
				Vector3 current = transform.PointToWorld(table.SamplePosition(i));

				// Each side of the piece as it is at the piece's middle: how far out its edge sits, and its border
				// as the segment's taper leaves it there (a bank, as much of it as stands there).
				float middle = (table.SampleDistance(i - 1) + table.SampleDistance(i)) * 0.5f;
				float sunk = deck ? 0.0f : profile.TrailDepth * fade.StrengthAt(middle);

				if (deck)
					pieces[i - 1] = new Piece(previous, current, new Side(width.LeftAt(middle) + overhang, 0.0f, 0.0f), new Side(width.RightAt(middle) + overhang, 0.0f, 0.0f));
				else if (banked)
					pieces[i - 1] = new Piece(previous, current, new Side(width.LeftAt(middle), border, 1.0f), new Side(width.RightAt(middle), border, 1.0f), sunk);
				else
					pieces[i - 1] = new Piece(previous, current,
						new Side(width.LeftAt(middle), border * taper.Left.WidthScaleAt(middle, table.Length, profile.FlatBorderWidth), taper.Left.ReliefAt(middle, table.Length, out _, out _, out _)),
						new Side(width.RightAt(middle), border * taper.Right.WidthScaleAt(middle, table.Length, profile.FlatBorderWidth), taper.Right.ReliefAt(middle, table.Length, out _, out _, out _)), sunk);

				previous = current;
			}

			var hash = StableHash.Create();

			hash.Add(deck ? 1 : 0);
			hash.Add(guard ? 1 : 0);
			hash.Add((int)profile.Border);
			hash.Add(profile.CurbHeight);
			hash.Add(profile.Drop);
			hash.Add(banked ? profile.BankHeight : 0.0f);
			hash.Add(reach);
			hash.Add(surfaceOffset);
			hash.Add(falloff);
			hash.Add(sink);

			foreach (var piece in pieces)
				piece.AddToHash(ref hash);

			var points = pieces.Select(piece => piece.From).Append(pieces[^1].To);

			shapers.Add(new Shaper { Profile = profile, Deck = deck, Guard = guard, Reach = reach, Pieces = pieces, Footprint = FootprintOf(points, reach, hash.Value) });
		}

		// The junction and parking lot surfaces and the pavements round them, and the rounded ends of roads that
		// stop: the ground is held under them and brought up to meet their kerbs, the same way a road holds it (and
		// banked up round a banked road's end). One whose roads all leave the ground alone leaves it alone too, and
		// only guards itself; a lot always shapes it.
		foreach (var area in _Network.GetGroundAreas())
		{
			var polygon = area.Polygon;

			if (polygon.Count < 3)
				continue;

			bool guard = area.Roads.Count > 0 && area.Roads.All(road => !road.ConformTerrain);
			Vector3 world = transform.PointToWorld(area.Centre);
			var outline = new Vector3[polygon.Count];
			float hold = MathF.Max(area.PavementReach, area.BankHeight > 0.0f ? area.BankWidth : 0.0f);
			var hash = StableHash.Create();

			hash.Add(guard ? 1 : 0);
			hash.Add(world);
			hash.Add(area.PavementReach);
			hash.Add(area.PavementTop);
			hash.Add(area.BankHeight);
			hash.Add(area.BankWidth);
			hash.Add(falloff);
			hash.Add(sink);

			for (int i = 0; i < polygon.Count; i++)
			{
				outline[i] = transform.PointToWorld(polygon[i]);
				hash.Add(outline[i]);
			}

			// The banks for each point of the outline, the same all round unless the area gives them point by point.
			var banks = new Vector2[polygon.Count];

			for (int i = 0; i < banks.Length; i++)
			{
				banks[i] = area.VertexBanks is { } given && i < given.Count ? given[i] : new Vector2(area.BankHeight, area.BankWidth);
				hash.Add(banks[i].x);
				hash.Add(banks[i].y);
			}

			shapers.Add(new Shaper
			{
				Guard = guard, Centre = world, Outline = outline, PavementReach = area.PavementReach, PavementTop = area.PavementTop, Banks = banks,
				Footprint = FootprintOf(outline, hold + (guard ? 0.0f : falloff), hash.Value)
			});
		}

		return shapers;
	}



	/// <summary>
	/// The rectangle in plan round some points, out by a reach all round.
	/// </summary>
	internal static RoadTerrainFootprint FootprintOf(IEnumerable<Vector3> _Points, float _Reach, long _Signature)
	{
		float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;

		foreach (var point in _Points)
		{
			minX = MathF.Min(minX, point.x);
			maxX = MathF.Max(maxX, point.x);
			minY = MathF.Min(minY, point.y);
			maxY = MathF.Max(maxY, point.y);
		}

		return new RoadTerrainFootprint(new Vector2(minX - _Reach, minY - _Reach), new Vector2(maxX + _Reach, maxY + _Reach), _Signature);
	}



	/// <summary>
	/// Shapes one terrain through its road layer and returns how many height samples changed: every sample within
	/// reach of a road is set from its ground (never from what an earlier conform left there), and every sample the
	/// roads held before but no longer reach gets its ground back. <paramref name="_Clip"/> keeps it to rectangles of
	/// the grid (Live Update, round what changed); without it the whole terrain is done. Without an undo, nothing is
	/// recorded and only <paramref name="_Changed"/> is sent to the terrain's texture and collider (see
	/// <see cref="RoadTerrainLayers.Lay"/>). <paramref name="_Held"/> is how many samples the roads hold afterwards.
	/// </summary>
	public static int Conform(Terrain _Terrain, RoadTerrainLayer _Layer, RoadNetwork _Network, IReadOnlyList<Shaper> _Shapers, IReadOnlyList<RectInt> _Clip, RoadTerrainUndo _Undo, out RectInt _Changed, out int _Held, out string _Problem)
	{
		_Changed = default;
		_Held = 0;
		_Problem = null;

		var storage = _Terrain.Storage;
		int resolution = storage.Resolution;

		if (resolution <= 0 || storage.HeightMap is null || storage.HeightMap.Length != resolution * resolution || storage.TerrainHeight <= 0.0f)
			return 0;

		float falloff = _Network.TerrainFalloff;
		float sink = _Network.TerrainSink;
		float surfaceOffset = _Network.SurfaceOffset;
		float cell = storage.TerrainSize / resolution;
		var terrainTransform = _Terrain.WorldTransform;
		IReadOnlyList<RectInt> clip = _Clip ?? new[] { new RectInt(0, 0, resolution, resolution) };
		var entries = new Dictionary<int, Entry>();
		var caps = new Dictionary<int, float>();
		var guards = new Dictionary<int, float>();
		var road = new Dictionary<int, Entry>();

		foreach (var shaper in _Shapers)
		{
			// Only what reaches the rectangles being redone is drawn over them.
			if (_Clip is not null && (RoadTerrainLayers.ToGrid(_Terrain, shaper.Footprint.Min, shaper.Footprint.Max) is not RectInt footprint || !RoadTerrainLayers.Overlaps(footprint, _Clip)))
				continue;

			road.Clear();

			// A road that leaves the ground alone pulls on nothing; it only says how high the others may bring the
			// ground under it (an area's pulls, worked out on the way, are simply dropped).
			if (shaper.Guard)
			{
				if (shaper.Outline is not null)
				{
					RasteriseArea(road, guards, terrainTransform, resolution, cell, clip, shaper.Centre, shaper.Outline, shaper.PavementReach, shaper.PavementTop, null, 0.0f, sink);
				}
				else
				{
					foreach (var piece in shaper.Pieces)
					{
						if (shaper.Deck)
							RasteriseDeck(guards, terrainTransform, resolution, cell, clip, piece, surfaceOffset, sink);
						else
							RasteriseGuard(guards, terrainTransform, resolution, cell, clip, piece, shaper.Profile, surfaceOffset, sink);
					}
				}

				continue;
			}

			if (shaper.Outline is not null)
			{
				RasteriseArea(road, caps, terrainTransform, resolution, cell, clip, shaper.Centre, shaper.Outline, shaper.PavementReach, shaper.PavementTop, shaper.Banks, falloff, sink);
			}
			else
			{
				foreach (var piece in shaper.Pieces)
				{
					if (shaper.Deck)
						RasteriseDeck(caps, terrainTransform, resolution, cell, clip, piece, surfaceOffset, sink);
					else
						Rasterise(road, caps, terrainTransform, resolution, cell, clip, piece, shaper.Reach, shaper.Profile, surfaceOffset, falloff, sink);
				}
			}

			// One road pulls on a sample once, from its nearest piece; roads pull together.
			foreach (var (index, pull) in road)
			{
				if (entries.TryGetValue(index, out var existing))
					entries[index] = new Entry(MathF.Min(existing.Distance, pull.Distance), existing.Distance <= pull.Distance ? existing.Anchor : pull.Anchor, existing.Remaining * pull.Remaining);
				else
					entries[index] = pull;
			}
		}

		// Every sample a road reaches is shaped from its ground (a guard alone reaches nothing); the layer gives the
		// rest it held their ground back.
		var reached = new HashSet<int>(entries.Keys);

		reached.UnionWith(caps.Keys);

		float heightScale = ushort.MaxValue / storage.TerrainHeight;
		float groundScale = storage.TerrainHeight / ushort.MaxValue;

		return RoadTerrainLayers.Lay(_Terrain, _Layer, RoadLayerMap.Heights, _Clip, reached, Shape, _Undo, out _Changed, out _Held, out _Problem);

		uint Shape(int _Index, uint _Ground)
		{
			// What the roads round this sample pull it to from its ground, capped by every road surface lying over
			// it: a sample under one road and in another's falloff is still under a road.
			var sample = new Vector3((_Index % resolution + 0.5f) * cell, (_Index / resolution + 0.5f) * cell, 0.0f);
			Vector3 world = terrainTransform.PointToWorld(sample);
			float ground = terrainTransform.PointToWorld(sample.WithZ(_Ground * groundScale)).z;
			float target = entries.TryGetValue(_Index, out var entry) ? MathX.Lerp(entry.Anchor, ground, entry.Remaining) : ground;

			if (caps.TryGetValue(_Index, out float cap))
				target = MathF.Min(target, cap);

			// Under a road that leaves the ground alone the others may still shape it, but never bring it up through
			// that road: no higher than just under it, or than the ground itself where that already stands higher.
			if (guards.TryGetValue(_Index, out float guard))
				target = MathF.Min(target, MathF.Max(ground, guard));

			// The target is a world height; the map stores heights above the terrain's own origin.
			float local = terrainTransform.PointToLocal(world.WithZ(target)).z;

			return (uint)Math.Clamp(MathF.Round(local * heightScale), 0.0f, ushort.MaxValue);
		}
	}



	/// <summary>
	/// Visits every height sample within reach of one straight piece of road, inside the clip, and records, for the
	/// road it is a piece of, what its nearest piece asks of the sample; and, for every sample under the road's
	/// surface, the height it must at least stay under.
	/// </summary>
	private static void Rasterise(Dictionary<int, Entry> _Road, Dictionary<int, float> _Caps, in Transform _TerrainTransform, int _Resolution, float _Cell, IReadOnlyList<RectInt> _Clip,
		in Piece _Piece, float _Reach, RoadProfile _Profile, float _SurfaceOffset, float _Falloff, float _Sink)
	{
		Vector3 from = _Piece.From;
		Vector3 to = _Piece.To;

		// The piece's footprint in terrain samples.
		Vector3 fromLocal = _TerrainTransform.PointToLocal(from);
		Vector3 toLocal = _TerrainTransform.PointToLocal(to);
		float localReach = _Reach / _TerrainTransform.Scale.x;

		int minX = Math.Clamp((int)MathF.Floor((MathF.Min(fromLocal.x, toLocal.x) - localReach) / _Cell), 0, _Resolution - 1);
		int maxX = Math.Clamp((int)MathF.Ceiling((MathF.Max(fromLocal.x, toLocal.x) + localReach) / _Cell), 0, _Resolution - 1);
		int minY = Math.Clamp((int)MathF.Floor((MathF.Min(fromLocal.y, toLocal.y) - localReach) / _Cell), 0, _Resolution - 1);
		int maxY = Math.Clamp((int)MathF.Ceiling((MathF.Max(fromLocal.y, toLocal.y) + localReach) / _Cell), 0, _Resolution - 1);

		if (minX > maxX || minY > maxY)
			return;

		var a = new Vector2(from.x, from.y);
		var b = new Vector2(to.x, to.y);
		Vector2 ab = b - a;
		float lengthSquared = ab.LengthSquared;

		foreach (var clip in _Clip)
		{
			int startX = Math.Max(minX, clip.Left);
			int endX = Math.Min(maxX, clip.Left + clip.Width - 1);
			int startY = Math.Max(minY, clip.Top);
			int endY = Math.Min(maxY, clip.Top + clip.Height - 1);

			for (int y = startY; y <= endY; y++)
			{
				for (int x = startX; x <= endX; x++)
				{
					Vector3 world = _TerrainTransform.PointToWorld(new Vector3((x + 0.5f) * _Cell, (y + 0.5f) * _Cell, 0.0f));
					var point = new Vector2(world.x, world.y);

					// Nearest point on the piece, the road height there, and which side of the road the sample is on
					// (its edge may sit further out on one side than the other).
					float t = lengthSquared > 0.0f ? Math.Clamp(Vector2.Dot(point - a, ab) / lengthSquared, 0.0f, 1.0f) : 0.0f;
					Vector2 nearest = a + ab * t;
					float distance = nearest.Distance(point);

					if (distance > _Reach)
						continue;

					bool left = ab.x * (point.y - a.y) - ab.y * (point.x - a.x) >= 0.0f;
					var side = left ? _Piece.Left : _Piece.Right;
					int index = x + y * _Resolution;
					float surface = MathX.Lerp(from.z, to.z, t) + _SurfaceOffset;
					float anchor = AnchorHeight(distance, surface, side.Edge, side.Border, side.Relief, _Profile, _Sink, _Piece.Sunk);

					// Under the road, and under a pavement, the ground can be no higher than this whatever another
					// road nearby asks for.
					bool covered = distance <= side.Edge + (_Profile.Border == RoadBorderStyle.Sidewalk ? side.Border : 0.0f);

					if (covered && (!_Caps.TryGetValue(index, out float existingCap) || anchor < existingCap))
						_Caps[index] = anchor;

					if (_Road.TryGetValue(index, out var existing) && existing.Distance <= distance)
						continue;

					// This road's pull on the sample: all of it out to the border's edge, fading over the falloff. The
					// roads' pulls are taken together afterwards, so between two roads the ground is held level.
					float beyond = MathF.Max(0.0f, distance - side.Edge - side.Border);
					float release = _Falloff > 0.0f ? Math.Clamp(beyond / _Falloff, 0.0f, 1.0f) : (beyond > 0.0f ? 1.0f : 0.0f);
					release = release * release * (3.0f - 2.0f * release);

					_Road[index] = new Entry(distance, anchor, release);
				}
			}
		}
	}



	/// <summary>
	/// Records, for every height sample under one straight piece of a road that leaves the ground alone (its surface
	/// and its border), inside the clip, how high the other roads may bring the ground there: just under the road,
	/// rising under the border the way the road would hold it if it shaped the ground itself.
	/// </summary>
	private static void RasteriseGuard(Dictionary<int, float> _Guards, in Transform _TerrainTransform, int _Resolution, float _Cell, IReadOnlyList<RectInt> _Clip,
		in Piece _Piece, RoadProfile _Profile, float _SurfaceOffset, float _Sink)
	{
		Vector3 from = _Piece.From;
		Vector3 to = _Piece.To;
		float leftReach = _Piece.Left.Edge + _Piece.Left.Border;
		float rightReach = _Piece.Right.Edge + _Piece.Right.Border;
		Vector3 fromLocal = _TerrainTransform.PointToLocal(from);
		Vector3 toLocal = _TerrainTransform.PointToLocal(to);
		float localReach = MathF.Max(leftReach, rightReach) / _TerrainTransform.Scale.x;

		int minX = Math.Clamp((int)MathF.Floor((MathF.Min(fromLocal.x, toLocal.x) - localReach) / _Cell), 0, _Resolution - 1);
		int maxX = Math.Clamp((int)MathF.Ceiling((MathF.Max(fromLocal.x, toLocal.x) + localReach) / _Cell), 0, _Resolution - 1);
		int minY = Math.Clamp((int)MathF.Floor((MathF.Min(fromLocal.y, toLocal.y) - localReach) / _Cell), 0, _Resolution - 1);
		int maxY = Math.Clamp((int)MathF.Ceiling((MathF.Max(fromLocal.y, toLocal.y) + localReach) / _Cell), 0, _Resolution - 1);

		if (minX > maxX || minY > maxY)
			return;

		var a = new Vector2(from.x, from.y);
		var b = new Vector2(to.x, to.y);
		Vector2 ab = b - a;
		float lengthSquared = ab.LengthSquared;

		foreach (var clip in _Clip)
		{
			int startX = Math.Max(minX, clip.Left);
			int endX = Math.Min(maxX, clip.Left + clip.Width - 1);
			int startY = Math.Max(minY, clip.Top);
			int endY = Math.Min(maxY, clip.Top + clip.Height - 1);

			for (int y = startY; y <= endY; y++)
			{
				for (int x = startX; x <= endX; x++)
				{
					Vector3 world = _TerrainTransform.PointToWorld(new Vector3((x + 0.5f) * _Cell, (y + 0.5f) * _Cell, 0.0f));
					var point = new Vector2(world.x, world.y);

					float t = lengthSquared > 0.0f ? Math.Clamp(Vector2.Dot(point - a, ab) / lengthSquared, 0.0f, 1.0f) : 0.0f;
					float distance = (a + ab * t).Distance(point);
					bool left = ab.x * (point.y - a.y) - ab.y * (point.x - a.x) >= 0.0f;
					var side = left ? _Piece.Left : _Piece.Right;

					if (distance > side.Edge + side.Border)
						continue;

					int index = x + y * _Resolution;
					float surface = MathX.Lerp(from.z, to.z, t) + _SurfaceOffset;
					float guard = AnchorHeight(distance, surface, side.Edge, side.Border, side.Relief, _Profile, _Sink, _Piece.Sunk);

					if (!_Guards.TryGetValue(index, out float existing) || guard < existing)
						_Guards[index] = guard;
				}
			}
		}
	}



	/// <summary>
	/// Keeps every height sample under a bridge's deck, inside the clip, below the road on it: the ground is not
	/// shaped to a bridge, but it must not come up through its deck where the bridge runs into the ground at its ends.
	/// </summary>
	private static void RasteriseDeck(Dictionary<int, float> _Caps, in Transform _TerrainTransform, int _Resolution, float _Cell, IReadOnlyList<RectInt> _Clip,
		in Piece _Piece, float _SurfaceOffset, float _Sink)
	{
		Vector3 from = _Piece.From;
		Vector3 to = _Piece.To;
		float leftReach = _Piece.Left.Edge;
		float rightReach = _Piece.Right.Edge;
		float reach = MathF.Max(leftReach, rightReach);
		Vector3 fromLocal = _TerrainTransform.PointToLocal(from);
		Vector3 toLocal = _TerrainTransform.PointToLocal(to);
		float localReach = reach / _TerrainTransform.Scale.x;

		int minX = Math.Clamp((int)MathF.Floor((MathF.Min(fromLocal.x, toLocal.x) - localReach) / _Cell), 0, _Resolution - 1);
		int maxX = Math.Clamp((int)MathF.Ceiling((MathF.Max(fromLocal.x, toLocal.x) + localReach) / _Cell), 0, _Resolution - 1);
		int minY = Math.Clamp((int)MathF.Floor((MathF.Min(fromLocal.y, toLocal.y) - localReach) / _Cell), 0, _Resolution - 1);
		int maxY = Math.Clamp((int)MathF.Ceiling((MathF.Max(fromLocal.y, toLocal.y) + localReach) / _Cell), 0, _Resolution - 1);

		if (minX > maxX || minY > maxY)
			return;

		var a = new Vector2(from.x, from.y);
		var b = new Vector2(to.x, to.y);
		Vector2 ab = b - a;
		float lengthSquared = ab.LengthSquared;

		foreach (var clip in _Clip)
		{
			int startX = Math.Max(minX, clip.Left);
			int endX = Math.Min(maxX, clip.Left + clip.Width - 1);
			int startY = Math.Max(minY, clip.Top);
			int endY = Math.Min(maxY, clip.Top + clip.Height - 1);

			for (int y = startY; y <= endY; y++)
			{
				for (int x = startX; x <= endX; x++)
				{
					Vector3 world = _TerrainTransform.PointToWorld(new Vector3((x + 0.5f) * _Cell, (y + 0.5f) * _Cell, 0.0f));
					var point = new Vector2(world.x, world.y);

					float t = lengthSquared > 0.0f ? Math.Clamp(Vector2.Dot(point - a, ab) / lengthSquared, 0.0f, 1.0f) : 0.0f;
					float distance = (a + ab * t).Distance(point);
					bool left = ab.x * (point.y - a.y) - ab.y * (point.x - a.x) >= 0.0f;

					if (distance > (left ? leftReach : rightReach))
						continue;

					int index = x + y * _Resolution;
					float cap = MathX.Lerp(from.z, to.z, t) + _SurfaceOffset - _Sink;

					if (!_Caps.TryGetValue(index, out float existingCap) || cap < existingCap)
						_Caps[index] = cap;
				}
			}
		}
	}



	/// <summary>
	/// Keeps every height sample under a junction's surface (a fan from its centre to its outline), inside the clip,
	/// below it, and every sample under the pavement round the outline just under the pavement's top. Beside a banked
	/// stretch of the outline (<paramref name="_Banks"/>, height and width at each of its points: a banked road's
	/// rounded end, its share of a junction) the ground rises to the bank's height over its width instead, the way it
	/// does beside the road.
	/// </summary>
	private static void RasteriseArea(Dictionary<int, Entry> _Area, Dictionary<int, float> _Caps, in Transform _TerrainTransform, int _Resolution, float _Cell, IReadOnlyList<RectInt> _Clip,
		Vector3 _Centre, Vector3[] _Vertices, float _PavementReach, float _PavementTop, Vector2[] _Banks, float _Falloff, float _Sink)
	{
		// A pavement round the area takes the place of any bank.
		bool banks = _PavementReach <= 0.0f && _Banks is not null && _Banks.Length == _Vertices.Length;

		// The furthest out from the outline the area holds the ground anywhere before letting it go over the falloff.
		float widest = _PavementReach;

		if (banks)
		{
			foreach (var bank in _Banks)
			{
				if (bank.x > 0.0f)
					widest = MathF.Max(widest, bank.y);
			}
		}

		float localReach = (widest + _Falloff) / _TerrainTransform.Scale.x;
		float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;

		foreach (var vertex in _Vertices)
		{
			Vector3 local = _TerrainTransform.PointToLocal(vertex);

			minX = MathF.Min(minX, local.x - localReach);
			maxX = MathF.Max(maxX, local.x + localReach);
			minY = MathF.Min(minY, local.y - localReach);
			maxY = MathF.Max(maxY, local.y + localReach);
		}

		int fromX = Math.Clamp((int)MathF.Floor(minX / _Cell), 0, _Resolution - 1);
		int toX = Math.Clamp((int)MathF.Ceiling(maxX / _Cell), 0, _Resolution - 1);
		int fromY = Math.Clamp((int)MathF.Floor(minY / _Cell), 0, _Resolution - 1);
		int toY = Math.Clamp((int)MathF.Ceiling(maxY / _Cell), 0, _Resolution - 1);

		foreach (var clip in _Clip)
		{
			int startX = Math.Max(fromX, clip.Left);
			int endX = Math.Min(toX, clip.Left + clip.Width - 1);
			int startY = Math.Max(fromY, clip.Top);
			int endY = Math.Min(toY, clip.Top + clip.Height - 1);

			for (int y = startY; y <= endY; y++)
			{
				for (int x = startX; x <= endX; x++)
				{
					Vector3 world = _TerrainTransform.PointToWorld(new Vector3((x + 0.5f) * _Cell, (y + 0.5f) * _Cell, 0.0f));
					var point = new Vector2(world.x, world.y);
					float distance;
					float edge;
					Vector2 bank = Vector2.Zero;

					if (FanHeight(_Centre, _Vertices, point) is { } height)
					{
						// Under the surface itself.
						distance = 0.0f;
						edge = height;
					}
					else
					{
						distance = OutlineDistance(_Vertices, point, out edge, out int nearest, out float along);

						if (distance > widest + _Falloff)
							continue;

						// The bank of the stretch of outline the sample is beside, as it is at the nearest point.
						if (banks)
							bank = Vector2.Lerp(_Banks[nearest], _Banks[(nearest + 1) % _Banks.Length], along);
					}

					bool banked = bank.x > 0.0f;

					// How far out from the outline the ground is held here before it is let go over the falloff.
					float hold = banked ? bank.y : _PavementReach;

					// Under the surface the ground is kept below it; under the pavement it comes up to meet its outer
					// edge; beside a bank it rises over the bank's width, quickly at first, to its height; beyond that
					// it is held there and let go over the falloff.
					float anchor;

					if (_PavementReach > 0.0f)
					{
						anchor = MathX.Lerp(edge - _Sink, edge + _PavementTop - 0.5f, Math.Clamp(distance / _PavementReach, 0.0f, 1.0f));
					}
					else if (distance <= 0.0f)
					{
						anchor = edge - _Sink;
					}
					else if (banked)
					{
						float rise = 1.0f - Math.Clamp(distance / MathF.Max(bank.y, 0.001f), 0.0f, 1.0f);

						anchor = edge + bank.x * (1.0f - rise * rise);
					}
					else
					{
						anchor = edge - 1.0f;
					}

					int index = x + y * _Resolution;

					if (distance <= _PavementReach && (!_Caps.TryGetValue(index, out float existingCap) || anchor < existingCap))
						_Caps[index] = anchor;

					float beyond = MathF.Max(0.0f, distance - hold);
					float release = _Falloff > 0.0f ? Math.Clamp(beyond / _Falloff, 0.0f, 1.0f) : (beyond > 0.0f ? 1.0f : 0.0f);
					release = release * release * (3.0f - 2.0f * release);

					if (_Area.TryGetValue(index, out var existing) && existing.Distance <= distance)
						continue;

					_Area[index] = new Entry(distance, anchor, release);
				}
			}
		}
	}



	/// <summary>
	/// How far a point in plan is from an outline, and the outline's height at the nearest point.
	/// </summary>
	internal static float OutlineDistance(Vector3[] _Vertices, Vector2 _Point, out float _Height)
	{
		return OutlineDistance(_Vertices, _Point, out _Height, out _, out _);
	}



	/// <summary>
	/// The same, with which edge of the outline (from that vertex to the next) the nearest point is on, and how far
	/// along it (0 at its start, 1 at its end).
	/// </summary>
	internal static float OutlineDistance(Vector3[] _Vertices, Vector2 _Point, out float _Height, out int _Edge, out float _Along)
	{
		float best = float.MaxValue;

		_Height = 0.0f;
		_Edge = 0;
		_Along = 0.0f;

		for (int i = 0; i < _Vertices.Length; i++)
		{
			Vector3 v0 = _Vertices[i];
			Vector3 v1 = _Vertices[(i + 1) % _Vertices.Length];
			var p0 = new Vector2(v0.x, v0.y);
			Vector2 edge = new Vector2(v1.x, v1.y) - p0;
			float lengthSquared = edge.LengthSquared;
			float t = lengthSquared > 0.0f ? Math.Clamp(Vector2.Dot(_Point - p0, edge) / lengthSquared, 0.0f, 1.0f) : 0.0f;
			float distance = (p0 + edge * t).Distance(_Point);

			if (distance >= best)
				continue;

			best = distance;
			_Height = MathX.Lerp(v0.z, v1.z, t);
			_Edge = i;
			_Along = t;
		}

		return best;
	}



	/// <summary>
	/// The height of the fan of triangles from a centre to an outline at a point in plan, or null outside it.
	/// </summary>
	internal static float? FanHeight(Vector3 _Centre, Vector3[] _Vertices, Vector2 _Point)
	{
		var c = new Vector2(_Centre.x, _Centre.y);

		for (int i = 0; i < _Vertices.Length; i++)
		{
			Vector3 v0 = _Vertices[i];
			Vector3 v1 = _Vertices[(i + 1) % _Vertices.Length];
			var p0 = new Vector2(v0.x, v0.y);
			var p1 = new Vector2(v1.x, v1.y);

			float area = Cross(p0 - c, p1 - c);

			if (MathF.Abs(area) < 0.0001f)
				continue;

			// Barycentric weights; a little slack so samples on the seams between triangles are not missed.
			float w0 = Cross(p1 - _Point, c - _Point) / area;
			float w1 = Cross(c - _Point, p0 - _Point) / area;
			float w2 = 1.0f - w0 - w1;

			if (w0 < -0.001f || w1 < -0.001f || w2 < -0.001f)
				continue;

			return w0 * v0.z + w1 * v1.z + w2 * _Centre.z;
		}

		return null;
	}



	private static float Cross(Vector2 _A, Vector2 _B)
	{
		return _A.x * _B.y - _A.y * _B.x;
	}



	/// <summary>
	/// The height a road holds the ground at, at a distance from its centreline: under the surface across the
	/// road, ramping under the border to meet the border's outer edge, and the edge's height from there on (how
	/// far that hold reaches before the untouched terrain takes over is the falloff, applied by the caller).
	/// <paramref name="_Sunk"/> is how far below its top the road's surface reaches there (its trails).
	/// </summary>
	private static float AnchorHeight(float _Distance, float _Surface, float _HalfWidth, float _Border, float _Relief, RoadProfile _Profile, float _Sink, float _Sunk)
	{
		float outer = _HalfWidth + _Border;
		float edge;

		// The border's relief is what it is here: sinking to nothing where the road type changes.
		switch (_Profile.Border)
		{
			case RoadBorderStyle.Sidewalk:
				// Flush with the pavement (a hair under, so its edge stays a clean line).
				edge = _Surface + _Profile.CurbHeight * _Relief - 0.5f;
				break;

			case RoadBorderStyle.CountrySide:
				// The ground meets the foot of the shoulder's drop; the verge slopes on down below it, so the
				// terrain runs through the verge and the two merge.
				edge = _Surface - _Profile.Drop * _Relief;
				break;

			default:
				// Bare edge: the ground meets the slab just under its lip.
				edge = _Surface - 1.0f;
				break;
		}

		// Under the road the ground is kept the sink below its surface, and below the bottom of its trails too where
		// they are cut.
		if (_Distance <= _HalfWidth)
			return _Surface - _Sink - _Sunk;

		// Beside a banked road the ground comes up over its edge, quickly at first, and on up to the bank's height (as
		// much of it as stands here); the road's skirt runs on under it, so wherever the two cross there is road below.
		if (_Profile.HasBanks)
		{
			float bank = _Surface + _Profile.BankHeight * _Relief;

			if (_Distance >= outer)
				return bank;

			float rise = 1.0f - (_Distance - _HalfWidth) / MathF.Max(_Border, 0.001f);

			return MathX.Lerp(_Surface, bank, 1.0f - rise * rise);
		}

		if (_Distance <= outer)
		{
			float across = (_Distance - _HalfWidth) / MathF.Max(_Border, 0.001f);

			switch (_Profile.Border)
			{
				case RoadBorderStyle.Sidewalk:
					// Hidden under the pavement, rising to meet its outer edge.
					return MathX.Lerp(_Surface - _Sink, edge, across);

				case RoadBorderStyle.CountrySide:
					// Hidden under the shoulder, coming up to the foot of the drop at its edge.
					return MathX.Lerp(_Surface - _Sink, edge, across);

				default:
					return edge;
			}
		}

		return edge;
	}
}

/// <summary>
/// Paints a terrain material along the roads. Every texel under a road and its border, and out by a margin from
/// there, takes the chosen material — at full strength inside that band, fading to nothing over the fade beyond
/// it, mixed into whatever the ground is painted with. Give a country road's verge the same texture and the two
/// merge into each other; a street gets a worn band round it.
/// </summary>
internal static class RoadTerrainPainter
{
	/// <summary>
	/// A control map texel names its two materials in five bits each, so a terrain can hold this many.
	/// </summary>
	private const int MaxMaterials = 32;

	/// <summary>
	/// One road's band of paint, or the area of a junction, parking lot or rounded dead end, in world space.
	/// </summary>
	internal sealed class Stroke
	{
		/// <summary>
		/// The road's curve samples; the band runs along the pieces between them.
		/// </summary>
		public Vector3[] Points { get; init; }

		/// <summary>
		/// How far out the band reaches at full strength on each side of each piece.
		/// </summary>
		public float[] Left { get; init; }
		public float[] Right { get; init; }

		public Vector3 Centre { get; init; }
		public Vector3[] Outline { get; init; }

		/// <summary>
		/// How far out from an area's outline it is painted at full strength.
		/// </summary>
		public float Band { get; init; }

		public RoadTerrainFootprint Footprint { get; init; }
	}



	/// <summary>
	/// Everything in a network that paints the ground, with the network's paint settings folded into each one's
	/// signature, so a change of material or fade touches all of them.
	/// </summary>
	public static List<Stroke> Collect(RoadNetwork _Network)
	{
		var material = _Network.TerrainPaintMaterial;
		float margin = _Network.TerrainPaintMargin;
		float fade = _Network.TerrainPaintFade;
		var transform = _Network.WorldTransform;
		var strokes = new List<Stroke>();

		foreach (var segment in _Network.Segments)
		{
			var table = _Network.GetCurveTable(segment);

			// A bridge carries its road over the ground rather than lying on it: what is under it is landscape.
			if (table is null || table.SampleCount < 2 || segment.Bridge is not null)
				continue;

			var profile = _Network.ProfileOf(segment);
			var taper = _Network.GetBorderTaper(segment);
			var width = new RoadWidth(segment, table.Length);
			float border = profile.FlatBorderWidth;
			var points = new Vector3[table.SampleCount];
			var left = new float[table.SampleCount - 1];
			var right = new float[table.SampleCount - 1];
			var hash = Signature(material, fade);

			for (int i = 0; i < table.SampleCount; i++)
			{
				points[i] = transform.PointToWorld(table.SamplePosition(i));
				hash.Add(points[i]);
			}

			for (int i = 1; i < table.SampleCount; i++)
			{
				// The band as the piece's middle has it: the road's edge on that side, its border as the segment's
				// taper leaves it there, and the margin.
				float middle = (table.SampleDistance(i - 1) + table.SampleDistance(i)) * 0.5f;

				left[i - 1] = width.LeftAt(middle) + border * taper.Left.WidthScaleAt(middle, table.Length, border) + margin;
				right[i - 1] = width.RightAt(middle) + border * taper.Right.WidthScaleAt(middle, table.Length, border) + margin;
				hash.Add(left[i - 1]);
				hash.Add(right[i - 1]);
			}

			float reach = MathF.Max(left.Max(), right.Max()) + fade;

			strokes.Add(new Stroke { Points = points, Left = left, Right = right, Footprint = RoadTerrainConformer.FootprintOf(points, reach, hash.Value) });
		}

		// A junction or a parking lot is a surface of its own: painted inside its outline and out from it.
		foreach (var (centre, polygon, pavementReach, _, _) in _Network.GetJunctionPolygons())
		{
			if (polygon.Count < 3)
				continue;

			Vector3 world = transform.PointToWorld(centre);
			var outline = new Vector3[polygon.Count];
			float band = pavementReach + margin;
			var hash = Signature(material, fade);

			hash.Add(world);
			hash.Add(band);

			for (int i = 0; i < polygon.Count; i++)
			{
				outline[i] = transform.PointToWorld(polygon[i]);
				hash.Add(outline[i]);
			}

			strokes.Add(new Stroke { Centre = world, Outline = outline, Band = band, Footprint = RoadTerrainConformer.FootprintOf(outline, band + fade, hash.Value) });
		}

		return strokes;
	}



	private static StableHash Signature(TerrainMaterial _Material, float _Fade)
	{
		var hash = StableHash.Create();

		hash.Add(_Material?.ResourcePath);
		hash.Add(_Fade);

		return hash;
	}



	/// <summary>
	/// Paints one terrain through its road layer and returns how many texels changed: every texel along a road is
	/// painted over its ground, and every texel the roads painted before but no longer reach gets its ground back.
	/// <paramref name="_Clip"/>, <paramref name="_Undo"/> and <paramref name="_Changed"/> work as they do for
	/// <see cref="RoadTerrainConformer.Conform"/>. <paramref name="_Held"/> is how many texels the roads hold
	/// afterwards; <paramref name="_Problem"/> is set when the terrain could not be painted for a reason the user
	/// needs telling about.
	/// </summary>
	public static int Paint(Terrain _Terrain, RoadTerrainLayer _Layer, RoadNetwork _Network, IReadOnlyList<Stroke> _Strokes, IReadOnlyList<RectInt> _Clip, RoadTerrainUndo _Undo, out RectInt _Changed, out int _Held, out string _Problem)
	{
		_Changed = default;
		_Held = 0;
		_Problem = null;

		var material = _Network.TerrainPaintMaterial;
		var storage = _Terrain.Storage;
		int resolution = storage.Resolution;

		if (material is null || resolution <= 0 || storage.ControlMap is null || storage.ControlMap.Length != resolution * resolution)
			return 0;

		float fade = _Network.TerrainPaintFade;
		float cell = storage.TerrainSize / resolution;
		var terrainTransform = _Terrain.WorldTransform;
		IReadOnlyList<RectInt> clip = _Clip ?? new[] { new RectInt(0, 0, resolution, resolution) };
		var coverage = new Dictionary<int, float>();

		foreach (var stroke in _Strokes)
		{
			// Only what reaches the rectangles being redone is drawn over them.
			if (_Clip is not null && (RoadTerrainLayers.ToGrid(_Terrain, stroke.Footprint.Min, stroke.Footprint.Max) is not RectInt footprint || !RoadTerrainLayers.Overlaps(footprint, _Clip)))
				continue;

			if (stroke.Outline is not null)
			{
				RasteriseArea(coverage, terrainTransform, resolution, cell, clip, stroke.Centre, stroke.Outline, stroke.Band, fade);
				continue;
			}

			for (int i = 1; i < stroke.Points.Length; i++)
				RasterisePiece(coverage, terrainTransform, resolution, cell, clip, stroke.Points[i - 1], stroke.Points[i], stroke.Left[i - 1], stroke.Right[i - 1], fade);
		}

		int id = storage.Materials.IndexOf(material);

		if (id < 0 && coverage.Count > 0)
		{
			if (storage.Materials.Count >= MaxMaterials)
			{
				_Problem = $"\"{_Terrain.GameObject.Name}\" already carries {MaxMaterials} terrain materials, which is all its control map can name.";
				return 0;
			}

			// The control map names materials by their place in the terrain's own list, so one the terrain has
			// never held joins the list first.
			storage.Materials.Add(material);
			_Terrain.UpdateMaterialsBuffer();

			id = storage.Materials.Count - 1;
		}

		// Every texel along a road is painted over its ground; the layer gives the rest it held their ground back.
		var reached = new HashSet<int>(coverage.Keys);

		return RoadTerrainLayers.Lay(_Terrain, _Layer, RoadLayerMap.Paint, _Clip, reached, Texel, _Undo, out _Changed, out _Held, out _Problem);

		uint Texel(int _Index, uint _Ground)
		{
			// A texel blends two materials, a base and an overlay over it, and the ground's own mix is kept as far as
			// two can hold it: where the ground already carries the material, its share grows; anywhere else it goes
			// on as the overlay, over whichever of the ground's two shows the most, so the band's edge is a blend
			// rather than a cut. Where it would show less than the material it pushes out, the ground is left alone.
			var texel = new CompactTerrainMaterial(_Ground);
			float strength = coverage[_Index];
			float overlay = texel.BlendFactor / 255.0f;

			if (texel.BaseTextureId == id)
			{
				overlay *= 1.0f - strength;
			}
			else if (texel.OverlayTextureId == id)
			{
				overlay = 1.0f - (1.0f - overlay) * (1.0f - strength);
			}
			else
			{
				float pushedOut = MathF.Min(overlay, 1.0f - overlay);

				if (strength < (1.0f - strength) * pushedOut)
					return _Ground;

				if (overlay > 0.5f)
					texel.BaseTextureId = texel.OverlayTextureId;

				texel.OverlayTextureId = (byte)id;
				overlay = strength;
			}

			texel.BlendFactor = (byte)Math.Clamp(MathF.Round(overlay * 255.0f), 0.0f, 255.0f);

			return texel.Packed;
		}
	}



	/// <summary>
	/// Visits every control map texel within reach of one straight piece of road, inside the clip, and records how
	/// much of the material it takes. A texel near two roads takes the most either of them gives it.
	/// </summary>
	private static void RasterisePiece(Dictionary<int, float> _Coverage, in Transform _TerrainTransform, int _Resolution, float _Cell, IReadOnlyList<RectInt> _Clip,
		Vector3 _From, Vector3 _To, float _Left, float _Right, float _Fade)
	{
		Vector3 fromLocal = _TerrainTransform.PointToLocal(_From);
		Vector3 toLocal = _TerrainTransform.PointToLocal(_To);
		float localReach = (MathF.Max(_Left, _Right) + _Fade) / _TerrainTransform.Scale.x;

		int minX = Math.Clamp((int)MathF.Floor((MathF.Min(fromLocal.x, toLocal.x) - localReach) / _Cell), 0, _Resolution - 1);
		int maxX = Math.Clamp((int)MathF.Ceiling((MathF.Max(fromLocal.x, toLocal.x) + localReach) / _Cell), 0, _Resolution - 1);
		int minY = Math.Clamp((int)MathF.Floor((MathF.Min(fromLocal.y, toLocal.y) - localReach) / _Cell), 0, _Resolution - 1);
		int maxY = Math.Clamp((int)MathF.Ceiling((MathF.Max(fromLocal.y, toLocal.y) + localReach) / _Cell), 0, _Resolution - 1);

		if (minX > maxX || minY > maxY)
			return;

		var a = new Vector2(_From.x, _From.y);
		var b = new Vector2(_To.x, _To.y);
		Vector2 ab = b - a;
		float lengthSquared = ab.LengthSquared;

		foreach (var clip in _Clip)
		{
			int startX = Math.Max(minX, clip.Left);
			int endX = Math.Min(maxX, clip.Left + clip.Width - 1);
			int startY = Math.Max(minY, clip.Top);
			int endY = Math.Min(maxY, clip.Top + clip.Height - 1);

			for (int y = startY; y <= endY; y++)
			{
				for (int x = startX; x <= endX; x++)
				{
					Vector3 world = _TerrainTransform.PointToWorld(new Vector3((x + 0.5f) * _Cell, (y + 0.5f) * _Cell, 0.0f));
					var point = new Vector2(world.x, world.y);

					float t = lengthSquared > 0.0f ? Math.Clamp(Vector2.Dot(point - a, ab) / lengthSquared, 0.0f, 1.0f) : 0.0f;
					float distance = (a + ab * t).Distance(point);

					// Which side the texel is on: a road's edge may sit further out on one side than the other.
					bool left = ab.x * (point.y - a.y) - ab.y * (point.x - a.x) >= 0.0f;

					Take(_Coverage, x + y * _Resolution, Strength(distance, left ? _Left : _Right, _Fade));
				}
			}
		}
	}



	/// <summary>
	/// The same for the surface of a junction or a parking lot: everything inside its outline, and out from it.
	/// </summary>
	private static void RasteriseArea(Dictionary<int, float> _Coverage, in Transform _TerrainTransform, int _Resolution, float _Cell, IReadOnlyList<RectInt> _Clip,
		Vector3 _Centre, Vector3[] _Vertices, float _Band, float _Fade)
	{
		float localReach = (_Band + _Fade) / _TerrainTransform.Scale.x;
		float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;

		foreach (var vertex in _Vertices)
		{
			Vector3 local = _TerrainTransform.PointToLocal(vertex);

			minX = MathF.Min(minX, local.x - localReach);
			maxX = MathF.Max(maxX, local.x + localReach);
			minY = MathF.Min(minY, local.y - localReach);
			maxY = MathF.Max(maxY, local.y + localReach);
		}

		int fromX = Math.Clamp((int)MathF.Floor(minX / _Cell), 0, _Resolution - 1);
		int toX = Math.Clamp((int)MathF.Ceiling(maxX / _Cell), 0, _Resolution - 1);
		int fromY = Math.Clamp((int)MathF.Floor(minY / _Cell), 0, _Resolution - 1);
		int toY = Math.Clamp((int)MathF.Ceiling(maxY / _Cell), 0, _Resolution - 1);

		foreach (var clip in _Clip)
		{
			int startX = Math.Max(fromX, clip.Left);
			int endX = Math.Min(toX, clip.Left + clip.Width - 1);
			int startY = Math.Max(fromY, clip.Top);
			int endY = Math.Min(toY, clip.Top + clip.Height - 1);

			for (int y = startY; y <= endY; y++)
			{
				for (int x = startX; x <= endX; x++)
				{
					Vector3 world = _TerrainTransform.PointToWorld(new Vector3((x + 0.5f) * _Cell, (y + 0.5f) * _Cell, 0.0f));
					var point = new Vector2(world.x, world.y);

					float distance = RoadTerrainConformer.FanHeight(_Centre, _Vertices, point) is not null
						? 0.0f
						: RoadTerrainConformer.OutlineDistance(_Vertices, point, out _);

					Take(_Coverage, x + y * _Resolution, Strength(distance, _Band, _Fade));
				}
			}
		}
	}



	private static void Take(Dictionary<int, float> _Coverage, int _Index, float _Strength)
	{
		if (_Strength <= 0.0f)
			return;

		if (!_Coverage.TryGetValue(_Index, out float existing) || _Strength > existing)
			_Coverage[_Index] = _Strength;
	}



	/// <summary>
	/// How much of the material a texel takes at a distance from a road: all of it out to the band's edge, then
	/// smoothly to nothing over the fade.
	/// </summary>
	private static float Strength(float _Distance, float _Band, float _Fade)
	{
		if (_Distance <= _Band)
			return 1.0f;

		if (_Fade <= 0.0f)
			return 0.0f;

		float beyond = Math.Clamp((_Distance - _Band) / _Fade, 0.0f, 1.0f);

		return 1.0f - beyond * beyond * (3.0f - 2.0f * beyond);
	}
}



file class RoadTerrainToolWidget : ToolSidebarWidget
{
	private readonly RoadTerrainTool m_Tool;
	private RoadNetwork m_Network;
	private WrappedLabel m_LayerStatus;
	private Checkbox m_Live;
	private WrappedLabel m_Result;
	private int m_ShownVersion = -1;
	private IDisposable m_UndoScope;



	public RoadTerrainToolWidget(RoadTerrainTool _Tool)
	{
		m_Tool = _Tool;

		Rebuild();
	}



	/// <summary>
	/// Lays the sidebar out for the network being edited: the settings are the network's own.
	/// </summary>
	private void Rebuild()
	{
		m_Network = m_Tool.Network;
		m_ShownVersion = -1;

		Layout.Clear(true);
		AddTitle("Terrain", "landscape");

		var layer = AddGroup("Road Layer");

		layer.Add(new WrappedLabel("Remembers the ground under everything the roads shape and paint, so both can be done again whenever the roads change, and taken back out. Start it once, on the terrain before any road is shaped into it."));
		m_LayerStatus = layer.Add(new WrappedLabel(""));

		m_Live = layer.Add(new Checkbox("Live Update")
		{
			ToolTip = "Keeps the terrain matching the roads while they are edited: a moment after a road changes, the ground round it is shaped (and painted, with a paint material set) again. Switching it on lays every road in first.",
			Value = m_Network.IsValid() && m_Network.TerrainLiveUpdate
		});

		m_Live.Clicked = () => m_Tool.SetLiveUpdate(m_Live.Value);

		layer.Add(new Button("Start Road Layer", "layers") { Clicked = m_Tool.StartRoadLayer });
		layer.Add(new Button("Remove Road Layer", "layers_clear") { Clicked = m_Tool.RemoveRoadLayer });

		if (m_Network.IsValid())
		{
			var serialized = m_Network.GetSerialized();

			serialized.OnPropertyStartEdit += _ => BeginEdit();
			serialized.OnPropertyFinishEdit += _ => EndEdit();

			var shaping = AddGroup("Shaping");

			SidebarRows.Add(shaping, serialized.GetProperty(nameof(RoadNetwork.TerrainFalloff)));
			SidebarRows.Add(shaping, serialized.GetProperty(nameof(RoadNetwork.TerrainSink)));
			shaping.Add(new WrappedLabel("The ground is cut and filled to meet every road and its border, then blends back over the falloff. Roads themselves do not move. Press it again whenever the roads change: it always starts from the road layer's ground."));
			shaping.Add(new Button("Conform Terrain To Roads", "landscape") { Clicked = m_Tool.ConformTerrain });

			var painting = AddGroup("Painting");

			SidebarRows.Add(painting, serialized.GetProperty(nameof(RoadNetwork.TerrainPaintMaterial)));
			SidebarRows.Add(painting, serialized.GetProperty(nameof(RoadNetwork.TerrainPaintMargin)));
			SidebarRows.Add(painting, serialized.GetProperty(nameof(RoadNetwork.TerrainPaintFade)));
			painting.Add(new WrappedLabel("Paints the material onto the terrain under every road and its border, out by the margin and fading from there. Give a country verge the same texture and the two merge."));
			painting.Add(new Button("Paint Terrain Along Roads", "brush") { Clicked = m_Tool.PaintTerrain });
		}
		else
		{
			AddGroup("Shaping").Add(new WrappedLabel("No road network in the scene yet: draw a road first."));
		}

		var nodes = AddGroup("Nodes");

		nodes.Add(new WrappedLabel("Drops every node onto the ground under it; the roads stay smooth between nodes."));
		nodes.Add(new Button("Snap Nodes To Ground", "vertical_align_bottom") { Clicked = m_Tool.SnapNodesToGround });

		var result = AddGroup("Result");

		m_Result = result.Add(new WrappedLabel(""));

		Layout.AddStretchCell();
	}



	[EditorEvent.Frame]
	public void Frame()
	{
		if (m_Tool.Network != m_Network)
		{
			EndEdit();
			Rebuild();
		}

		string status = m_Tool.LayerStatus;

		if (status != m_LayerStatus.Text)
			m_LayerStatus.Text = status;

		// An undo or redo can switch Live Update without the checkbox being clicked.
		bool live = m_Network.IsValid() && m_Network.TerrainLiveUpdate;

		if (m_Live.Value != live)
			m_Live.Value = live;

		if (m_Tool.ResultVersion == m_ShownVersion)
			return;

		m_ShownVersion = m_Tool.ResultVersion;
		m_Result.Text = m_Tool.LastResult;
	}



	private void BeginEdit()
	{
		if (!m_Network.IsValid())
			return;

		m_UndoScope ??= SceneEditorSession.Active.UndoScope("Edit Terrain Settings").WithComponentChanges(m_Network).Push();
	}



	private void EndEdit()
	{
		m_UndoScope?.Dispose();
		m_UndoScope = null;
	}
}
