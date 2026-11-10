using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace RedSnail.RoadTool2;

public sealed partial class RoadNetwork
{
	/// <summary>
	/// Bump whenever the generated geometry changes (UV layout, sampling, profile...), so pieces built by an older
	/// generator are replaced on the next reconcile instead of lingering until their road is edited.
	/// </summary>
	private const int GeneratorVersion = 76;

	/// <summary>
	/// A stretch of road shorter than this is not worth a chunk (it happens when a junction swallows a whole chunk).
	/// </summary>
	private const float MinChunkLength = 1.0f;

	/// <summary>
	/// Where a segment's painted lines may run: between <see cref="Start"/> and <see cref="End"/>, skipping the
	/// gap between <see cref="GapStart"/> and <see cref="GapEnd"/> when there is one (a crossing in the middle of
	/// a short road between two junctions).
	/// </summary>
	private readonly record struct LineRange(float Start, float End, float GapStart, float GapEnd)
	{
		public bool HasGap => GapEnd > GapStart;

		public void AddToHash(ref StableHash _Hash)
		{
			_Hash.Add(Start);
			_Hash.Add(End);
			_Hash.Add(GapStart);
			_Hash.Add(GapEnd);
		}
	}

	/// <summary>
	/// A segment's pedestrian crossings: at either end, or one shared crossing halfway along a short road between
	/// two junctions (recorded on the start end). Centres are curve distances from the segment's start.
	/// </summary>
	private readonly record struct SegmentCrossings(bool AtStart, float StartCentre, bool AtEnd, float EndCentre);

	/// <summary>
	/// One piece of a segment to build. <see cref="Lines"/> bounds the painted lines; <see cref="Taper"/> is the
	/// border's run-out into a different road type at either end; <see cref="Bridge"/> is the whole built range
	/// and the ends of a bridge piece; <see cref="Trails"/> is where a surface's trails rise out at its ends, and
	/// <see cref="Blend"/> where another surface runs on into it; the other kinds ignore them.
	/// </summary>
	private readonly record struct ChunkPlan(int Index, int Count, float Start, float End, RoadChunkKind Kind, BorderCaps Caps, RoadBorderBuilder.SegmentBorderBlends Blends, long Signature, LineRange Lines = default, SegmentBorderTapers Taper = default, RoadBridgeBuilder.BridgeSpan Bridge = default, RoadPropBuilder.PropSpan Props = default, RoadMarkingBuilder.DashPhase Dash = default, SegmentCrossings Crossings = default, RoadTrailFade Trails = default, RoadSurfaceBlend Blend = default);

	private readonly record struct PendingGroup(long Signature, int Remaining);

	/// <summary>
	/// One piece to (re)build: a chunk of a segment, or, when <see cref="NodeId"/> is set, a piece of a junction.
	/// <see cref="ArmBlends"/> and <see cref="ArmTapers"/> carry, for a junction's border piece, what each arm's
	/// chunks were built with, keyed by segment; <see cref="ArmSurfaces"/>, for its surface, each arm's road's blend.
	/// </summary>
	private readonly record struct RebuildJob(int SegmentId, int NodeId, RoadChunkKind Kind, long GroupSignature, ChunkPlan Plan, RoadCurveTable Table, RoadWidth Width, RoadProfile Profile, RoadJunctionLayout Junction, Dictionary<int, RoadBorderBuilder.SegmentBorderBlends> ArmBlends, Dictionary<int, SegmentBorderTapers> ArmTapers, RoadLot Lot,
		Dictionary<int, RoadSurfaceBlend> ArmSurfaces = null);

	/// <summary>
	/// One piece the queue builds: a chunk of a segment, a piece of a junction or a piece of a lot.
	/// </summary>
	private readonly record struct PieceKey(RoadChunkKind Kind, int SegmentId, int NodeId, int LotId, int Index);

	private bool m_IsDirty;
	private Queue<RebuildJob> m_Jobs = new();

	/// <summary>
	/// For each piece waiting in the queue, the tick it was last built on, or first asked for if it has not been
	/// since. The queue is kept in this order, so while a drag requeues the same pieces every frame the budget goes
	/// round all of them instead of rebuilding the first few over and over.
	/// </summary>
	private readonly Dictionary<PieceKey, long> m_PieceStamps = new();
	private long m_TickCount;
	private readonly Dictionary<int, PendingGroup> m_PendingSegments = new();
	private readonly Dictionary<(int NodeId, RoadChunkKind Kind), PendingGroup> m_PendingJunctions = new();

	/// <summary>
	/// Junction layouts keyed by node, each with a hash of what it was computed from. Finding corners on curved
	/// edges is the one expensive step of a reconcile, and while a node is dragged only its own junction and its
	/// neighbours' change.
	/// </summary>
	private readonly Dictionary<int, (long Key, RoadJunctionLayout Layout)> m_JunctionCache = new();

	/// <summary>
	/// True while pieces are still queued for (re)generation.
	/// </summary>
	public bool IsRebuilding => m_Jobs.Count > 0;



	/// <summary>
	/// Asks for a reconcile pass on the next editor frame: the graph is compared against the pieces that exist and
	/// only what differs gets queued for regeneration.
	/// </summary>
	public void MarkDirty()
	{
		m_IsDirty = true;

		// Undo, reloads and the inspector all land here with a graph nobody told us about piece by piece.
		InvalidateIndex();
	}



	/// <summary>
	/// Asks the network in the editor's scene for a reconcile. Editing a road type, a bridge, a prop set or a
	/// parking layout changes roads that hold no copy of it, so the asset tells the network rather than the other
	/// way round.
	/// </summary>
	public static void Editor_MarkDirty()
	{
		var scene = Application.Editor?.Scene;

		if (!scene.IsValid())
			return;

		var network = scene.Get<RoadNetwork>();

		if (!network.IsValid())
			return;

		network.MarkDirty();
	}



	/// <summary>
	/// One reconcile pass, then as much of the queued work as this frame's budget allows. Driven from the
	/// component's own update in RoadNetwork.cs.
	/// </summary>
	private void Tick()
	{
		m_TickCount++;

		if (m_IsDirty)
		{
			m_IsDirty = false;
			Reconcile();
			ReorderJobs();
		}

		ProcessJobs();
	}



	private static PieceKey KeyOf(in RebuildJob _Job)
	{
		if (_Job.Lot is not null)
			return new PieceKey(_Job.Kind, -1, -1, _Job.Lot.Id, 0);

		if (_Job.NodeId >= 0)
			return new PieceKey(_Job.Kind, -1, _Job.NodeId, -1, 0);

		return new PieceKey(_Job.Kind, _Job.SegmentId, -1, -1, _Job.Plan.Index);
	}



	/// <summary>
	/// Whether a queued job is still the latest asked for its piece: a reconcile that finds the inputs changed again
	/// queues the piece anew and leaves the old job behind.
	/// </summary>
	private bool IsCurrent(in RebuildJob _Job)
	{
		if (_Job.Lot is not null)
			return m_PendingLots.TryGetValue((_Job.Lot.Id, _Job.Kind), out var lot) && lot.Signature == _Job.GroupSignature;

		if (_Job.NodeId >= 0)
			return m_PendingJunctions.TryGetValue((_Job.NodeId, _Job.Kind), out var junction) && junction.Signature == _Job.GroupSignature;

		return m_PendingSegments.TryGetValue(_Job.SegmentId, out var segment) && segment.Signature == _Job.GroupSignature;
	}



	/// <summary>
	/// Drops the jobs a reconcile has superseded and puts the rest in order of how long their piece has waited, the
	/// ones built longest ago first. Without it, a drag that changes the same pieces every frame only ever reaches
	/// the first few in the queue (the first roads in the list), and the junction it moves waits until the drag
	/// stops.
	/// </summary>
	private void ReorderJobs()
	{
		if (m_Jobs.Count == 0)
		{
			m_PieceStamps.Clear();
			return;
		}

		var current = new List<(RebuildJob Job, long Stamp)>(m_Jobs.Count);
		var waiting = new HashSet<PieceKey>();

		foreach (var job in m_Jobs)
		{
			if (!IsCurrent(job))
				continue;

			var key = KeyOf(job);

			if (!m_PieceStamps.TryGetValue(key, out long stamp))
			{
				stamp = m_TickCount;
				m_PieceStamps[key] = stamp;
			}

			waiting.Add(key);
			current.Add((job, stamp));
		}

		// Stable, so pieces that have waited as long keep the order the reconcile gave them.
		m_Jobs = new Queue<RebuildJob>(current.OrderBy(entry => entry.Stamp).Select(entry => entry.Job));

		foreach (var key in m_PieceStamps.Keys.Where(key => !waiting.Contains(key)).ToList())
			m_PieceStamps.Remove(key);
	}



	public IEnumerable<RoadChunk> GetChunks()
	{
		foreach (var child in GameObject.Children)
		{
			if (!child.IsValid() || child.IsDestroyed)
				continue;

			var chunk = child.Components.Get<RoadChunk>();

			if (chunk is not null)
				yield return chunk;
		}
	}



	private RoadChunk FindChunk(int _SegmentId, int _ChunkIndex, RoadChunkKind _Kind)
	{
		return GetChunks().FirstOrDefault(chunk => !chunk.IsJunction && chunk.SegmentId == _SegmentId && chunk.ChunkIndex == _ChunkIndex && chunk.Kind == _Kind);
	}



	private RoadChunk FindJunctionChunk(int _NodeId, RoadChunkKind _Kind)
	{
		return GetChunks().FirstOrDefault(chunk => chunk.IsJunction && chunk.NodeId == _NodeId && chunk.Kind == _Kind);
	}



	/// <summary>
	/// Compares the graph with the pieces under this GameObject and queues work for everything that is missing,
	/// stale or built from different settings. Stateless on purpose: it copes with undo, reloads, hand deleted
	/// pieces and hot reloads alike, because the pieces themselves carry what they were built from.
	/// </summary>
	private void Reconcile()
	{
		PruneCurveTables();

		// A lot's entrances belong to its edge, wherever the lot has been moved to since.
		SnapAllOpenings();

		var segmentChunks = new Dictionary<int, List<RoadChunk>>();
		var junctionChunks = new Dictionary<(int NodeId, RoadChunkKind Kind), RoadChunk>();
		var lotChunks = new Dictionary<(int LotId, RoadChunkKind Kind), RoadChunk>();

		foreach (var chunk in GetChunks())
		{
			if (chunk.IsLot)
			{
				if (lotChunks.TryGetValue((chunk.LotId, chunk.Kind), out var duplicate))
					duplicate.GameObject.Destroy();

				lotChunks[(chunk.LotId, chunk.Kind)] = chunk;
				continue;
			}

			if (chunk.IsJunction)
			{
				// Two pieces for one junction can only be leftovers; keep one, the reconcile sorts out whether it is current.
				if (junctionChunks.TryGetValue((chunk.NodeId, chunk.Kind), out var duplicate))
					duplicate.GameObject.Destroy();

				junctionChunks[(chunk.NodeId, chunk.Kind)] = chunk;
				continue;
			}

			if (!segmentChunks.TryGetValue(chunk.SegmentId, out var list))
			{
				list = new List<RoadChunk>();
				segmentChunks[chunk.SegmentId] = list;
			}

			list.Add(chunk);
		}

		var layouts = ComputeJunctionLayouts();
		var dashPhases = ComputeDashPhases();
		var liveSegments = new HashSet<int>();
		var segmentBlends = new Dictionary<int, RoadBorderBuilder.SegmentBorderBlends>();
		var segmentTapers = new Dictionary<int, SegmentBorderTapers>();
		var segmentSurfaces = new Dictionary<int, RoadSurfaceBlend>();

		foreach (var segment in Segments)
		{
			var table = GetCurveTable(segment);

			if (table is null)
				continue;

			liveSegments.Add(segment.Id);

			var profile = ProfileOf(segment);
			// A road opening into a parking lot starts where the lot's surface stops covering it.
			float trimStart = MathF.Max(layouts.GetValueOrDefault(segment.StartNodeId)?.FindArm(segment.Id)?.Trim ?? 0.0f, LotTrimAt(segment.StartNodeId));
			float trimEnd = MathF.Max(layouts.GetValueOrDefault(segment.EndNodeId)?.FindArm(segment.Id)?.Trim ?? 0.0f, LotTrimAt(segment.EndNodeId));
			var caps = ComputeBorderCaps(segment, layouts);
			var blends = ComputeBorderBlends(segment, table, layouts);
			var taper = ComputeBorderTaper(segment, table, layouts, trimStart, trimEnd);
			var crossings = ComputeCrossings(segment, table, layouts, trimStart, trimEnd);
			var lines = ComputeLineRange(table, profile, trimStart, trimEnd, crossings);
			var dash = dashPhases.GetValueOrDefault(segment.Id);
			var trails = ComputeTrailFade(segment, table, profile, layouts, trimStart, trimEnd);
			var surfaceBlend = ComputeSurfaceBlend(segment, table, profile, layouts, trimStart, trimEnd);

			var plans = PlanChunks(segment, table, trimStart, trimEnd, profile, caps, blends, taper, ComputeSurfaceSignature(segment, table.Curve, profile), lines, dash, crossings, trails, surfaceBlend);
			long groupSignature = CombineSignatures(plans);

			segmentBlends[segment.Id] = blends;
			segmentTapers[segment.Id] = taper;
			segmentSurfaces[segment.Id] = surfaceBlend;
			segmentChunks.TryGetValue(segment.Id, out var existing);

			if (IsSegmentUpToDate(existing, plans))
				continue;

			// Already queued with these exact inputs: let those jobs finish rather than doubling them.
			if (m_PendingSegments.TryGetValue(segment.Id, out var pending) && pending.Signature == groupSignature)
				continue;

			EnqueueSegment(segment, table, profile, plans, groupSignature, existing);
		}

		foreach (var (nodeId, layout) in layouts)
		{
			// A road blending the junction's surface into its own starts doing so across its mouth, inside the junction.
			var armSurfaces = new Dictionary<int, RoadSurfaceBlend>();

			foreach (var arm in layout.Arms)
				armSurfaces[arm.Segment.Id] = segmentSurfaces.GetValueOrDefault(arm.Segment.Id);

			ReconcileJunctionPiece(nodeId, layout, RoadChunkKind.Surface, ComputeJunctionSurfaceSignature(layout, armSurfaces), junctionChunks, null, null, armSurfaces);

			bool anyBorder = layout.Arms.Any(arm => ProfileOf(arm.Segment).HasBorder);

			if (anyBorder)
			{
				// The junction's border runs continue the arms' chunks, so they take the same lifts and tapers those
				// were built with.
				var armBlends = new Dictionary<int, RoadBorderBuilder.SegmentBorderBlends>();
				var armTapers = new Dictionary<int, SegmentBorderTapers>();

				foreach (var arm in layout.Arms)
				{
					armBlends[arm.Segment.Id] = segmentBlends.GetValueOrDefault(arm.Segment.Id);
					armTapers[arm.Segment.Id] = segmentTapers.GetValueOrDefault(arm.Segment.Id);
				}

				ReconcileJunctionPiece(nodeId, layout, RoadChunkKind.Border, ComputeJunctionBorderSignature(layout, armBlends, armTapers), junctionChunks, armBlends, armTapers);
			}
			else if (junctionChunks.TryGetValue((nodeId, RoadChunkKind.Border), out var border))
			{
				border.GameObject.Destroy();
			}

			// Crossings used to be decals hung on the junction; the roads paint their own now, so anything left
			// here from then goes.
			if (junctionChunks.TryGetValue((nodeId, RoadChunkKind.Marking), out var marking))
				marking.GameObject.Destroy();
		}

		ReconcileLots(lotChunks);

		// Whatever no longer belongs to anything in the graph goes, locked or not.
		foreach (var (segmentId, chunks) in segmentChunks)
		{
			if (liveSegments.Contains(segmentId))
				continue;

			foreach (var chunk in chunks)
				chunk.GameObject.Destroy();

			m_PendingSegments.Remove(segmentId);
		}

		foreach (var (key, chunk) in junctionChunks)
		{
			if (layouts.ContainsKey(key.NodeId))
				continue;

			chunk.GameObject.Destroy();
			m_PendingJunctions.Remove(key);
		}
	}



	private void ReconcileJunctionPiece(int _NodeId, RoadJunctionLayout _Layout, RoadChunkKind _Kind, long _Signature, Dictionary<(int NodeId, RoadChunkKind Kind), RoadChunk> _Existing, Dictionary<int, RoadBorderBuilder.SegmentBorderBlends> _ArmBlends, Dictionary<int, SegmentBorderTapers> _ArmTapers = null,
		Dictionary<int, RoadSurfaceBlend> _ArmSurfaces = null)
	{
		var key = (_NodeId, _Kind);

		if (_Existing.TryGetValue(key, out var existing) && (existing.Locked || (existing.Signature == _Signature && !LacksDebugRecord(existing))))
			return;

		if (m_PendingJunctions.TryGetValue(key, out var pending) && pending.Signature == _Signature)
			return;

		m_Jobs.Enqueue(new RebuildJob(-1, _NodeId, _Kind, _Signature, default, null, RoadWidth.Uniform(0.0f), null, _Layout, _ArmBlends, _ArmTapers, null, _ArmSurfaces));
		m_PendingJunctions[key] = new PendingGroup(_Signature, 1);
	}



	/// <summary>
	/// Where every segment sits in its run: the stretch of road carried on through pass-through nodes until a
	/// junction, a road end or a loop. Each run is walked once, from the lowest-numbered segment in it backwards
	/// to its head and then forwards to its tail, in that segment's direction, so every segment of the run agrees
	/// on where the run's distance starts and the dashes carry across the nodes.
	/// </summary>
	private Dictionary<int, RoadMarkingBuilder.DashPhase> ComputeDashPhases()
	{
		var phases = new Dictionary<int, RoadMarkingBuilder.DashPhase>();

		foreach (var seed in Segments.OrderBy(segment => segment.Id))
		{
			if (phases.ContainsKey(seed.Id))
				continue;

			var seen = new HashSet<int> { seed.Id };
			var run = new List<(RoadSegment Segment, bool Forward)>();

			// Back to the head: a road carrying on through the node runs with the run when it ends at that node.
			var segment = seed;
			int nodeId = seed.StartNodeId;

			while (RunContinuation(segment, nodeId) is { } previous && seen.Add(previous.Id))
			{
				bool forward = previous.EndNodeId == nodeId;

				run.Add((previous, forward));
				segment = previous;
				nodeId = forward ? previous.StartNodeId : previous.EndNodeId;
			}

			run.Reverse();
			run.Add((seed, true));

			// On to the tail.
			segment = seed;
			nodeId = seed.EndNodeId;

			while (RunContinuation(segment, nodeId) is { } next && seen.Add(next.Id))
			{
				bool forward = next.StartNodeId == nodeId;

				run.Add((next, forward));
				segment = next;
				nodeId = forward ? next.EndNodeId : next.StartNodeId;
			}

			float offset = 0.0f;

			foreach (var (member, forward) in run)
			{
				phases[member.Id] = new RoadMarkingBuilder.DashPhase(offset, !forward);
				offset += GetCurveTable(member)?.Length ?? 0.0f;
			}
		}

		return phases;
	}



	/// <summary>
	/// The road a segment carries straight on into at a pass-through node, or null at anything else.
	/// </summary>
	private RoadSegment RunContinuation(RoadSegment _Segment, int _NodeId)
	{
		if (GetDegree(_NodeId) != 2)
			return null;

		return GetSegmentsAt(_NodeId).FirstOrDefault(candidate => candidate.Id != _Segment.Id);
	}



	/// <summary>
	/// Where a segment's trails rise out of it: flat at each end where its surface stops (a junction, a bend sharp
	/// enough to be one, a parking lot, a rounded end, another road type, a bridge), at full depth the profile's fade
	/// length in from there, never more than half the stretch between the two. A road of the same type and width
	/// running on the same way through a plain pass-through node lays its trails exactly where these leave off, so
	/// they carry straight on across it. A bridge's deck takes no trails.
	/// </summary>
	private RoadTrailFade ComputeTrailFade(RoadSegment _Segment, RoadCurveTable _Table, RoadProfile _Profile, Dictionary<int, RoadJunctionLayout> _Layouts, float _TrimStart, float _TrimEnd)
	{
		if (_Segment.Bridge is not null || !_Profile.IsShaped)
			return default;

		float start = _TrimStart;
		float end = _Table.Length - _TrimEnd;

		if (end - start <= 0.01f)
			return default;

		float length = MathF.Min(_Profile.TrailFade, (end - start) * 0.5f);
		bool startCarries = TrailsCarryOn(_Segment, _Table, _Profile, _Segment.StartNodeId, true, _Layouts);
		bool endCarries = TrailsCarryOn(_Segment, _Table, _Profile, _Segment.EndNodeId, false, _Layouts);

		return new RoadTrailFade(startCarries, start, endCarries, end, length);
	}



	/// <summary>
	/// Whether a segment's trails carry straight on across one of its ends: a plain pass-through node (no junction
	/// there, no lot), into a road with the same trails, no deck, the same way round (a trail lies to one side of the
	/// way the road runs, and wanders to that side) and the same width each side (trails are kept inside the road, so a
	/// narrower one could hold them elsewhere).
	/// </summary>
	private bool TrailsCarryOn(RoadSegment _Segment, RoadCurveTable _Table, RoadProfile _Profile, int _NodeId, bool _AtStart, Dictionary<int, RoadJunctionLayout> _Layouts)
	{
		if (_Layouts is not null && _Layouts.GetValueOrDefault(_NodeId) is not null)
			return false;

		if (LotAtOpening(_NodeId) is not null)
			return false;

		var other = RunContinuation(_Segment, _NodeId);

		if (other is null || other.Bridge is not null)
			return false;

		// Running the same way: into this segment's start comes the other's end, out of its end goes the other's start.
		bool otherStarts = other.StartNodeId == _NodeId;

		if (otherStarts == _AtStart)
			return false;

		if (ProfileOf(other).TrailsSignature != _Profile.TrailsSignature)
			return false;

		var otherTable = GetCurveTable(other);

		if (otherTable is null)
			return false;

		var width = new RoadWidth(_Segment, _Table.Length);
		var otherWidth = new RoadWidth(other, otherTable.Length);
		float distance = _AtStart ? 0.0f : _Table.Length;
		float otherDistance = otherStarts ? 0.0f : otherTable.Length;

		return MathF.Abs(width.LeftAt(distance) - otherWidth.LeftAt(otherDistance)) < 0.5f && MathF.Abs(width.RightAt(distance) - otherWidth.RightAt(otherDistance)) < 0.5f;
	}



	/// <summary>
	/// Where every segment's trails are cut into it, by segment id, exactly as its surface is built: for the tools that
	/// shape the ground to the roads, which keep it below the trails only where they are.
	/// </summary>
	public Dictionary<int, RoadTrailFade> GetTrailFades()
	{
		var layouts = ComputeJunctionLayouts();
		var fades = new Dictionary<int, RoadTrailFade>();

		foreach (var segment in Segments)
		{
			var table = GetCurveTable(segment);

			if (table is null)
				continue;

			float trimStart = MathF.Max(layouts.GetValueOrDefault(segment.StartNodeId)?.FindArm(segment.Id)?.Trim ?? 0.0f, LotTrimAt(segment.StartNodeId));
			float trimEnd = MathF.Max(layouts.GetValueOrDefault(segment.EndNodeId)?.FindArm(segment.Id)?.Trim ?? 0.0f, LotTrimAt(segment.EndNodeId));
			var profile = ProfileOf(segment);

			fades[segment.Id] = profile.HasTrails ? ComputeTrailFade(segment, table, profile, layouts, trimStart, trimEnd) : default;
		}

		return fades;
	}



	/// <summary>
	/// Where another surface runs on into a segment's own: from each end where its surface stops against another
	/// material (a junction laid in another road type's surface, a parking lot's, or another road type carrying on
	/// from a plain pass-through node), over the profile's blend length, never more than half the stretch between the
	/// two ends. At a junction the blend starts back across the road's mouth, where its share of the corners starts and
	/// its skirt and banks with it (see <see cref="RoadMouthBlend"/>), so the junction's surface is already giving way
	/// where the road's edges begin. Nothing without a blend material, or on a bridge's deck.
	/// </summary>
	private RoadSurfaceBlend ComputeSurfaceBlend(RoadSegment _Segment, RoadCurveTable _Table, RoadProfile _Profile, Dictionary<int, RoadJunctionLayout> _Layouts, float _TrimStart, float _TrimEnd)
	{
		if (_Profile.RoadBlendMaterial is null || _Segment.Bridge is not null)
			return default;

		if (_Table.Length - _TrimEnd - _TrimStart <= 0.01f)
			return default;

		float start = MouthAt(true) ?? _TrimStart;
		float end = MouthAt(false) ?? _Table.Length - _TrimEnd;
		float length = MathF.Min(_Profile.RoadBlendLength, (end - start) * 0.5f);
		var width = new RoadWidth(_Segment, _Table.Length);
		var (scale, offset) = RoadProfile.TextureTransform(_Profile.ResolvedRoadMaterial);
		Vector2 second = RoadProfile.SecondLayerScale(_Profile.RoadBlendMaterial);
		float repeat = MathF.Max(1.0f, _Profile.RoadTextureRepeat);
		var startSource = SourceAt(true, start);
		var endSource = SourceAt(false, end);

		return new RoadSurfaceBlend(startSource is not null, start, endSource is not null, end, length, startSource ?? default, endSource ?? default);

		RoadBlendSource? SourceAt(bool _AtStart, float _Stop)
		{
			if (SurfaceMet(_Segment, _Table, _Profile, _AtStart, _Layouts) is not { } met)
				return null;

			// Taken round by whole repeats to start out as near as it can to where the road's own texture is at the
			// stop, on its centreline (as the blend's second layer would take it), so the blend has as little to make
			// up as it moves from one to the other.
			float left = width.LeftAt(_Stop);
			float right = width.RightAt(_Stop);
			float centreU = _Profile.RoadTextureFitsWidth ? (left + right > 0.001f ? left / (left + right) : 0.5f) : 0.0f;
			var own = new Vector2((centreU * scale.x + offset.x) * second.x, (_Stop / repeat * scale.y + offset.y) * second.y);
			Vector2 theirs = met.SampleAt(_Stop, 0.0f, left, right, _Table.PositionAt(_Stop));

			return met with { Shift = new Vector2(MathF.Round(own.x - theirs.x), MathF.Round(own.y - theirs.y)) };
		}

		// Where the line across the road's mouth crosses its centreline, for an end at a junction.
		float? MouthAt(bool _AtStart)
		{
			int nodeId = _AtStart ? _Segment.StartNodeId : _Segment.EndNodeId;

			if (_Layouts?.GetValueOrDefault(nodeId) is not { } layout)
				return null;

			var arm = layout.Arms.FirstOrDefault(candidate => candidate.Segment.Id == _Segment.Id && candidate.AtStart == _AtStart);

			if (arm is null || !RoadJunctionBuilder.Mouth(layout, arm, JunctionMainArm(layout), out float centre, out _))
				return null;

			return arm.CurveDistance(centre);
		}
	}



	/// <summary>
	/// The surface a segment's own stops against at one of its ends, when it is another material, as it is laid there:
	/// a junction's (laid in its widest road's surface, carried on into each arm along the arm's own curve), the
	/// parking lot's it opens into (fitted to the road through its mouth, or flat from above), or the road's carrying
	/// on from a plain pass-through node (its own mapping, run on past its end). Null for none.
	/// </summary>
	private RoadBlendSource? SurfaceMet(RoadSegment _Segment, RoadCurveTable _Table, RoadProfile _Profile, bool _AtStart, Dictionary<int, RoadJunctionLayout> _Layouts)
	{
		int nodeId = _AtStart ? _Segment.StartNodeId : _Segment.EndNodeId;
		string own = _Profile.ResolvedRoadMaterial?.ResourcePath;

		if (_Layouts is not null && _Layouts.GetValueOrDefault(nodeId) is { } layout)
		{
			var main = JunctionMainArm(layout);

			if (main is null || main.Segment.Id == _Segment.Id)
				return null;

			var junction = ProfileOf(main.Segment);

			return junction.ResolvedRoadMaterial?.ResourcePath == own ? null : RoadBlendSource.Along(junction, 0.0f, false);
		}

		if (LotAtOpening(nodeId) is { } lot)
		{
			var surface = ProfileOf(lot);

			if (surface.ResolvedRoadMaterial?.ResourcePath == own)
				return null;

			return surface.RoadTextureFitsWidth ? RoadBlendSource.Along(surface, 0.0f, false) : RoadBlendSource.Flat(surface);
		}

		var other = RunContinuation(_Segment, nodeId);
		var otherTable = other is null ? null : GetCurveTable(other);

		if (otherTable is null || ProfileOf(other).ResolvedRoadMaterial?.ResourcePath == own)
			return null;

		// The other road's distance along its own curve, from this one's: the same way on from the node, or back the
		// other way when the two run away from (or into) it together.
		bool otherStarts = other.StartNodeId == nodeId;
		bool reversed = otherStarts == _AtStart;
		float at = _AtStart ? 0.0f : _Table.Length;
		float otherAt = otherStarts ? 0.0f : otherTable.Length;

		return RoadBlendSource.Along(ProfileOf(other), otherAt - (reversed ? -at : at), reversed);
	}



	/// <summary>
	/// How a segment's border runs out at each end where it meets another road type's: over the profile's transition
	/// length, towards the cross-section the two share. Without the junction layouts only pass-through nodes are
	/// known; the reconcile passes them, so junction corners taper too.
	/// </summary>
	public SegmentBorderTapers GetBorderTaper(RoadSegment _Segment)
	{
		var table = GetCurveTable(_Segment);

		return table is null ? default : ComputeBorderTaper(_Segment, table);
	}



	/// <summary>
	/// One end of a border's taper: how long it is, the width and share of relief it runs to, whether a country road's
	/// gravel spills over it, and how far from the node it ends.
	/// </summary>
	private readonly record struct EndTaper(float Length, float Width, float Relief, bool Spill, float Offset);



	private SegmentBorderTapers ComputeBorderTaper(RoadSegment _Segment, RoadCurveTable _Table, Dictionary<int, RoadJunctionLayout> _Layouts = null, float _TrimStart = 0.0f, float _TrimEnd = 0.0f)
	{
		var profile = ProfileOf(_Segment);

		if (!profile.HasBorder)
			return default;

		float limit = _Table.Length * 0.5f;
		RoadBorderTaper left = default;
		RoadBorderTaper right = default;

		// A transition ends at its trim, so the two ends share the stretch BETWEEN the trims and each gets half of
		// it; however far a junction reaches along the road (a ramp meeting a highway can trim most of it), the two
		// never run into each other and neither is squeezed out.
		float transitionLimit = MathF.Max(0.0f, _Table.Length - _TrimStart - _TrimEnd) * 0.5f;

		if (profile.TransitionLength > 0.0f)
		{
			SideTapers(_Segment, profile, _Segment.StartNodeId, true, transitionLimit, _Layouts, ref left, ref right);
			SideTapers(_Segment, profile, _Segment.EndNodeId, false, transitionLimit, _Layouts, ref left, ref right);
		}

		// At a nose, the border on the side facing the road this one parts from grows out of nothing instead.
		if (GoreLength(_Segment, _Table, _Segment.StartNodeId, true, limit) is { } startLeft)
			left = left with { StartLength = startLeft, StartWidth = 0.0f, StartRelief = 0.0f, StartSpill = false, StartGore = true, StartOffset = 0.0f };

		if (GoreLength(_Segment, _Table, _Segment.StartNodeId, false, limit) is { } startRight)
			right = right with { StartLength = startRight, StartWidth = 0.0f, StartRelief = 0.0f, StartSpill = false, StartGore = true, StartOffset = 0.0f };

		if (GoreLength(_Segment, _Table, _Segment.EndNodeId, true, limit) is { } endLeft)
			left = left with { EndLength = endLeft, EndWidth = 0.0f, EndRelief = 0.0f, EndSpill = false, EndGore = true, EndOffset = 0.0f };

		if (GoreLength(_Segment, _Table, _Segment.EndNodeId, false, limit) is { } endRight)
			right = right with { EndLength = endRight, EndWidth = 0.0f, EndRelief = 0.0f, EndSpill = false, EndGore = true, EndOffset = 0.0f };

		return new SegmentBorderTapers(left, right);
	}



	/// <summary>
	/// The tapers at one end of a segment, one per side, each towards the border across from it there. At a plain
	/// pass-through node that is the other road on both sides. At a junction it is the road on the far side of each
	/// corner, and the taper ends at the arm's trim, where the corner starts: the corner itself is then built in the
	/// one cross-section both its roads have run to, so it is a clean curve rather than one border turning into
	/// the other round the arc.
	/// </summary>
	private void SideTapers(RoadSegment _Segment, RoadProfile _Profile, int _NodeId, bool _AtStart, float _Limit, Dictionary<int, RoadJunctionLayout> _Layouts, ref RoadBorderTaper _Left, ref RoadBorderTaper _Right)
	{
		int degree = GetDegree(_NodeId);

		if (degree < 2)
			return;

		RoadSegment leftNeighbour = null;
		RoadSegment rightNeighbour = null;
		float trim = 0.0f;

		if (_Layouts is not null && _Layouts.TryGetValue(_NodeId, out var layout) && layout.FindArm(_Segment.Id) is { } arm)
		{
			trim = arm.Trim;

			// A corner starting on this arm runs on its left looking out of the node, one ending on it on its right;
			// looking along a segment that ends at the node swaps the two.
			foreach (var path in layout.CornerPaths)
			{
				if (path.ArmA.Segment.Id == _Segment.Id)
				{
					if (_AtStart)
						leftNeighbour = path.ArmB.Segment;
					else
						rightNeighbour = path.ArmB.Segment;
				}
				else if (path.ArmB.Segment.Id == _Segment.Id)
				{
					if (_AtStart)
						rightNeighbour = path.ArmA.Segment;
					else
						leftNeighbour = path.ArmA.Segment;
				}
			}
		}
		else if (degree == 2)
		{
			leftNeighbour = GetSegmentsAt(_NodeId).FirstOrDefault(candidate => candidate.Id != _Segment.Id);
			rightNeighbour = leftNeighbour;
		}

		if (SideTaper(_Profile, leftNeighbour, degree, trim, _Limit, out var leftEnd))
			_Left = WithEnd(_Left, _AtStart, leftEnd);

		if (SideTaper(_Profile, rightNeighbour, degree, trim, _Limit, out var rightEnd))
			_Right = WithEnd(_Right, _AtStart, rightEnd);
	}



	/// <summary>
	/// The taper one side of a border takes towards the border across from it at a node, or false when it takes none:
	/// nothing across, a border that is already the same size, or — at a junction — a border of another kind, which
	/// a junction still closes off against this one.
	/// </summary>
	private bool SideTaper(RoadProfile _Profile, RoadSegment _Neighbour, int _Degree, float _Trim, float _Limit, out EndTaper _Taper)
	{
		_Taper = default;

		if (_Neighbour is null)
			return false;

		var other = ProfileOf(_Neighbour);
		bool sameKind = other.Border == _Profile.Border;

		if (_Degree > 2 && !sameKind)
			return false;

		if (sameKind && MathF.Abs(other.FlatBorderWidth - _Profile.FlatBorderWidth) < 0.01f && MathF.Abs(other.ReliefHeight - _Profile.ReliefHeight) < 0.01f)
			return false;

		// Held to its half of the stretch between the trims, so it never runs into the taper at the far end.
		float length = MathF.Min(_Profile.TransitionLength, _Limit);

		if (length <= 0.01f)
			return false;

		_Taper = new EndTaper(length, RoadProfile.MeetingWidth(_Profile, other), RoadProfile.MeetingRelief(_Profile, other), !sameKind && other.Border == RoadBorderStyle.CountrySide, _Trim);

		return true;
	}



	private static RoadBorderTaper WithEnd(in RoadBorderTaper _Taper, bool _AtStart, in EndTaper _End)
	{
		return _AtStart
			? _Taper with { StartLength = _End.Length, StartWidth = _End.Width, StartRelief = _End.Relief, StartSpill = _End.Spill, StartOffset = _End.Offset }
			: _Taper with { EndLength = _End.Length, EndWidth = _End.Width, EndRelief = _End.Relief, EndSpill = _End.Spill, EndOffset = _End.Offset };
	}



	/// <summary>
	/// How far from a node the gap between this segment's edge on one side (its own left or right) and the edge of
	/// another road parting from the same point has opened enough for both roads' borders to fit side by side; null
	/// when no road parts from that edge there. Sampled along both roads at equal distances from the node, which
	/// is how two roads leaving a common tangent separate.
	/// </summary>
	private float? GoreLength(RoadSegment _Segment, RoadCurveTable _Table, int _NodeId, bool _Left, float _Limit)
	{
		const float step = 25.0f;

		if (GetDegree(_NodeId) < 3)
			return null;

		bool atStart = _Segment.StartNodeId == _NodeId;
		float own = ProfileOf(_Segment).FlatBorderWidth;
		var width = new RoadWidth(_Segment, _Table.Length);

		// This segment's edge point at a distance from the node, on the side asked for.
		Vector3 OwnEdge(float _FromNode)
		{
			float distance = atStart ? _FromNode : _Table.Length - _FromNode;

			RoadSurfaceBuilder.CrossSection(_Table, distance, width, 0.0f, out Vector3 leftEdge, out Vector3 rightEdge);

			return _Left ? leftEdge : rightEdge;
		}

		Vector3 start = OwnEdge(0.0f);

		foreach (var other in GetSegmentsAt(_NodeId))
		{
			if (other.Id == _Segment.Id)
				continue;

			var otherTable = GetCurveTable(other);

			if (otherTable is null)
				continue;

			bool otherAtStart = other.StartNodeId == _NodeId;
			var otherWidth = new RoadWidth(other, otherTable.Length);

			Vector3 OtherEdge(float _FromNode, bool _OtherLeft)
			{
				float distance = otherAtStart ? _FromNode : otherTable.Length - _FromNode;

				RoadSurfaceBuilder.CrossSection(otherTable, distance, otherWidth, 0.0f, out Vector3 leftEdge, out Vector3 rightEdge);

				return _OtherLeft ? leftEdge : rightEdge;
			}

			// The other road's edge that starts on ours, if either does.
			bool otherLeft;

			if (OtherEdge(0.0f, true).Distance(start) < 1.0f)
				otherLeft = true;
			else if (OtherEdge(0.0f, false).Distance(start) < 1.0f)
				otherLeft = false;
			else
				continue;

			// Parting means leaving the node the same way; a road turning off at an angle is a corner, not a gore.
			Vector3 ownWay = OwnEdge(step) - start;
			Vector3 otherWay = OtherEdge(step, otherLeft) - start;

			if (ownWay.IsNearZeroLength || otherWay.IsNearZeroLength || Vector3.Dot(ownWay.Normal, otherWay.Normal) < 0.7f)
				continue;

			float needed = own + ProfileOf(other).FlatBorderWidth;
			float reach = MathF.Min(_Limit, MathF.Min(_Table.Length, otherTable.Length));

			for (float fromNode = step; fromNode <= reach; fromNode += step)
			{
				if (OwnEdge(fromNode).Distance(OtherEdge(fromNode, otherLeft)) >= needed)
					return MathF.Max(step, fromNode);
			}

			return MathF.Max(step, reach);
		}

		return null;
	}



	/// <summary>
	/// Whether two road types' borders come to one cross-section where they meet, so nothing needs closing off
	/// between them: of a kind, and both tapering into the width and height they share.
	/// </summary>
	private static bool MeetsFlush(RoadProfile _A, RoadProfile _B)
	{
		return _A.HasBorder && _A.Border == _B.Border && _A.TransitionLength > 0.0f && _B.TransitionLength > 0.0f;
	}



	/// <summary>
	/// The filled area of every junction, in network space (lifted to the road surface): its centre and its
	/// outline, which the junction surface fans between, plus how far a pavement reaches out from the outline
	/// round it, how far above the outline that pavement's top sits (nothing when no arm has a sidewalk), and the
	/// roads it belongs to (a rounded end its own road, a junction its arms, a parking lot none). For tools that need
	/// to know where road is, and whose: the terrain conformer leaves alone an area whose roads all leave the ground
	/// alone.
	/// </summary>
	public IEnumerable<(Vector3 Centre, IReadOnlyList<Vector3> Polygon, float PavementReach, float PavementTop, IReadOnlyList<RoadSegment> Roads)> GetJunctionPolygons()
	{
		return GetGroundAreas().Select(area => (area.Centre, area.Polygon, area.PavementReach, area.PavementTop, area.Roads));
	}



	/// <summary>
	/// The same, with the banks the ground rises in round a banked road's rounded end (see <see cref="RoadGroundArea"/>).
	/// </summary>
	public IEnumerable<RoadGroundArea> GetGroundAreas()
	{
		// A parking lot is a junction as far as the ground under it is concerned.
		foreach (var lot in Lots)
		{
			var profile = ProfileOf(lot);
			bool pavement = profile.Border == RoadBorderStyle.Sidewalk;

			yield return new RoadGroundArea(lot.Centre, RoadLotOutline.Build(lot, OpeningsOf(lot)).Points, pavement ? profile.BorderWidth : 0.0f, pavement ? profile.CurbHeight : 0.0f, 0.0f, 0.0f, s_NoSegments);
		}

		// A rounded dead end reaches past its node, where no road piece covers the ground.
		foreach (var segment in Segments)
		{
			var table = GetCurveTable(segment);

			if (table is null || segment.Bridge is not null)
				continue;

			var profile = ProfileOf(segment);
			bool pavement = profile.Border == RoadBorderStyle.Sidewalk;
			var width = new RoadWidth(segment, table.Length);

			foreach (bool atStart in new[] { true, false })
			{
				int nodeId = atStart ? segment.StartNodeId : segment.EndNodeId;

				if (GetDegree(nodeId) != 1 || LotAtOpening(nodeId) is not null)
					continue;

				var outline = RoadCapBuilder.Outline(table, atStart ? 0.0f : table.Length, atStart, width, profile, SurfaceOffset, out Vector3 centre);

				if (outline.Count >= 3)
					yield return new RoadGroundArea(centre, outline, pavement ? profile.BorderWidth : 0.0f, pavement ? profile.CurbHeight : 0.0f,
						profile.HasBanks ? profile.BankHeight : 0.0f, profile.HasBanks ? profile.BankWidth : 0.0f, new[] { segment });
			}
		}

		foreach (var layout in ComputeJunctionLayouts().Values)
		{
			if (layout.Polygon.Count < 3)
				continue;

			float reach = 0.0f;
			float top = 0.0f;

			foreach (var arm in layout.Arms)
			{
				var profile = ProfileOf(arm.Segment);

				if (profile.Border != RoadBorderStyle.Sidewalk)
					continue;

				reach = MathF.Max(reach, profile.BorderWidth);
				top = MathF.Max(top, profile.CurbHeight);
			}

			var banks = JunctionBanks(layout, out float bankHeight, out float bankWidth);

			yield return new RoadGroundArea(layout.Centre, layout.Polygon, reach, top, bankHeight, bankWidth, layout.Arms.Select(arm => arm.Segment).ToList(), banks);
		}
	}



	/// <summary>
	/// The banks round a junction, for each point of its outline: a banked road's (see <see cref="RoadProfile.BankHeight"/>)
	/// carry on along every stretch of the outline it owns (its row, and its half of the corner either side, which
	/// its skirt runs under), so the ground comes over the junction's kerb there as it does over the road's, up to
	/// where the corner hands over to the next road. A point between a banked stretch and another takes the bank, so
	/// the banked stretch is banked all the way along. Null when no road at the junction has banks.
	/// </summary>
	private List<Vector2> JunctionBanks(RoadJunctionLayout _Layout, out float _Height, out float _Width)
	{
		_Height = 0.0f;
		_Width = 0.0f;

		var polygon = _Layout.Polygon;
		var owners = _Layout.PolygonArms;

		if (owners is null || owners.Count != polygon.Count)
			return null;

		var edges = new Vector2[polygon.Count];

		for (int i = 0; i < polygon.Count; i++)
		{
			if (owners[i] < 0)
				continue;

			var arm = _Layout.Arms[owners[i]];
			var profile = ProfileOf(arm.Segment);

			if (arm.Segment.Bridge is not null || !profile.HasBanks)
				continue;

			edges[i] = new Vector2(profile.BankHeight, profile.BankWidth);
			_Height = MathF.Max(_Height, profile.BankHeight);
			_Width = MathF.Max(_Width, profile.BankWidth);
		}

		if (_Height <= 0.0f)
			return null;

		// Each point takes the edge leaving it or the one coming into it, whichever is banked more.
		var banks = new List<Vector2>(polygon.Count);

		for (int i = 0; i < polygon.Count; i++)
		{
			Vector2 leaving = edges[i];
			Vector2 coming = edges[(i + polygon.Count - 1) % polygon.Count];

			banks.Add(leaving.x >= coming.x ? leaving : coming);
		}

		return banks;
	}



	/// <summary>
	/// Lays out every junction. A road too short for the fillets at both its ends gets those fillets shrunk until
	/// their tangent points fit on it (a trim past the tangent point would send the corner geometry back over the
	/// road); only corners that cannot fit even sharp are clamped. Then the junction polygons are built.
	/// </summary>
	private Dictionary<int, RoadJunctionLayout> ComputeJunctionLayouts()
	{
		var radii = Nodes.ToDictionary(node => node.Id, node => MathF.Max(0.0f, node.CornerRadius));
		var layouts = BuildJunctionLayouts(radii);
		var shrink = new Dictionary<int, float>();

		foreach (var segment in Segments)
		{
			var table = GetCurveTable(segment);

			if (table is null)
				continue;

			var startArm = layouts.GetValueOrDefault(segment.StartNodeId)?.FindArm(segment.Id);
			var endArm = layouts.GetValueOrDefault(segment.EndNodeId)?.FindArm(segment.Id);
			float sharp = (startArm?.SharpTrim ?? 0.0f) + (endArm?.SharpTrim ?? 0.0f);
			float fillet = (startArm is null ? 0.0f : startArm.RawTrim - startArm.SharpTrim) + (endArm is null ? 0.0f : endArm.RawTrim - endArm.SharpTrim);
			float available = MathF.Max(0.0f, table.Length - MinChunkLength);

			if (fillet <= 0.0f || sharp + fillet <= available)
				continue;

			// The fillet's share of a trim is proportional to the radius, so this factor makes it fit exactly.
			float factor = Math.Clamp((available - sharp) / fillet, 0.0f, 1.0f);

			foreach (int nodeId in new[] { segment.StartNodeId, segment.EndNodeId })
			{
				if (layouts.ContainsKey(nodeId))
					shrink[nodeId] = MathF.Min(shrink.GetValueOrDefault(nodeId, 1.0f), factor);
			}
		}

		if (shrink.Count > 0)
		{
			foreach (var (nodeId, factor) in shrink)
				radii[nodeId] *= factor;

			layouts = BuildJunctionLayouts(radii);
		}

		// Last resort, for corners that do not fit even when sharp: share what road there is.
		foreach (var segment in Segments)
		{
			var table = GetCurveTable(segment);

			if (table is null)
				continue;

			var startArm = layouts.GetValueOrDefault(segment.StartNodeId)?.FindArm(segment.Id);
			var endArm = layouts.GetValueOrDefault(segment.EndNodeId)?.FindArm(segment.Id);
			float total = (startArm?.Trim ?? 0.0f) + (endArm?.Trim ?? 0.0f);
			float available = MathF.Max(0.0f, table.Length - MinChunkLength);

			if (total <= available || total <= 0.0f)
				continue;

			float scale = available / total;

			if (startArm is not null)
				startArm.Trim *= scale;

			if (endArm is not null)
				endArm.Trim *= scale;
		}

		foreach (var layout in layouts.Values)
			layout.BuildPolygon();

		return layouts;
	}



	/// <summary>
	/// The corner fold on one side of a segment at one of its nodes, and whether the segment is that fold's first
	/// arm (its left edge) or second (its right edge). Looking out of the node, a segment starting there has its
	/// left on the arm's left; one ending there has it on the arm's right.
	/// </summary>
	private static RoadJunctionLayout.CornerFold FindFold(RoadSegment _Segment, int _NodeId, bool _AtStart, bool _Left, Dictionary<int, RoadJunctionLayout> _Layouts, out bool _SideA)
	{
		_SideA = _AtStart == _Left;

		if (!_Layouts.TryGetValue(_NodeId, out var layout))
			return null;

		var path = _SideA
			? layout.CornerPaths.FirstOrDefault(candidate => candidate.ArmA.Segment.Id == _Segment.Id)
			: layout.CornerPaths.FirstOrDefault(candidate => candidate.ArmB.Segment.Id == _Segment.Id);

		return path?.Fold;
	}



	/// <summary>
	/// The junction layout of every node for the given fillet radii, from the cache where the inputs match.
	/// </summary>
	private Dictionary<int, RoadJunctionLayout> BuildJunctionLayouts(Dictionary<int, float> _Radii)
	{
		var layouts = new Dictionary<int, RoadJunctionLayout>();
		var liveNodes = new HashSet<int>();

		foreach (var node in Nodes)
		{
			liveNodes.Add(node.Id);

			float radius = _Radii[node.Id];
			long key = ComputeJunctionKey(node, radius);
			RoadJunctionLayout layout;

			// A cached layout is only as good as its references: undo and reloads swap the graph's objects.
			if (m_JunctionCache.TryGetValue(node.Id, out var cached) && cached.Key == key && (cached.Layout is null || cached.Layout.Refresh(this)))
			{
				layout = cached.Layout;
			}
			else
			{
				layout = RoadJunctionLayout.Create(this, node, SurfaceOffset, radius);
				m_JunctionCache[node.Id] = (key, layout);
			}

			if (layout is null)
				continue;

			layout.ResetTrims();
			layouts[node.Id] = layout;
		}

		foreach (int nodeId in m_JunctionCache.Keys.Where(id => !liveNodes.Contains(id)).ToList())
			m_JunctionCache.Remove(nodeId);

		return layouts;
	}



	/// <summary>
	/// Whether an end of a road asks for a pedestrian crossing: the road type has them, the road has not turned
	/// this end's off, and the end is a real junction (three roads or more; a bend has a layout too, but nobody
	/// crosses at a bend) that trims the road.
	/// </summary>
	private bool WantsCrossing(RoadSegment _Segment, int _NodeId, Dictionary<int, RoadJunctionLayout> _Layouts, float _Trim)
	{
		if (!ProfileOf(_Segment).Crossings || !_Segment.CrossingAt(_NodeId))
			return false;

		return _Layouts.TryGetValue(_NodeId, out var layout) && layout.Arms.Count >= 3 && _Trim > 0.01f;
	}



	/// <summary>
	/// Places a road's crossings on the stretch left between its junctions. Two crossings need room for both and
	/// a gap between; a shorter road between two junctions gets one crossing shared by both, halfway along; a road
	/// shorter than a crossing gets none.
	/// </summary>
	private SegmentCrossings ComputeCrossings(RoadSegment _Segment, RoadCurveTable _Table, Dictionary<int, RoadJunctionLayout> _Layouts, float _TrimStart, float _TrimEnd)
	{
		var profile = ProfileOf(_Segment);
		float depth = profile.CrossingDepth;
		float clear = _Table.Length - _TrimStart - _TrimEnd;
		bool wantStart = WantsCrossing(_Segment, _Segment.StartNodeId, _Layouts, _TrimStart);
		bool wantEnd = WantsCrossing(_Segment, _Segment.EndNodeId, _Layouts, _TrimEnd);

		if (clear < depth || (!wantStart && !wantEnd))
			return default;

		// A ladder of stripes is laid on the road outside the junction; two lines mark the walk across the
		// junction's own mouth, so that kind is pulled in far enough to put its outer line on the pavement's
		// corner instead of a crossing's depth short of it.
		bool inside = profile.CrossingStyle == RoadCrossingStyle.Lines;
		float startCentre = inside
			? MathF.Max(depth * 0.5f, _TrimStart - depth * 0.5f)
			: _TrimStart + depth * 0.5f;

		float endCentre = inside
			? MathF.Min(_Table.Length - depth * 0.5f, _Table.Length - _TrimEnd + depth * 0.5f)
			: _Table.Length - _TrimEnd - depth * 0.5f;

		if (wantStart && wantEnd)
		{
			if (clear >= 2.0f * depth + profile.CrossingClearance)
				return new SegmentCrossings(true, startCentre, true, endCentre);

			return new SegmentCrossings(true, _TrimStart + clear * 0.5f, false, 0.0f);
		}

		return wantStart
			? new SegmentCrossings(true, startCentre, false, 0.0f)
			: new SegmentCrossings(false, 0.0f, true, endCentre);
	}



	/// <summary>
	/// Where the painted lines run: the stretch between the junctions, kept clear of the crossings.
	/// </summary>
	private static LineRange ComputeLineRange(RoadCurveTable _Table, RoadProfile _Profile, float _TrimStart, float _TrimEnd, in SegmentCrossings _Crossings)
	{
		float reach = _Profile.CrossingDepth * 0.5f + _Profile.CrossingClearance;
		float start = _TrimStart;
		float end = _Table.Length - _TrimEnd;
		float gapStart = 0.0f;
		float gapEnd = 0.0f;

		if (_Crossings.AtEnd)
			end = MathF.Min(end, _Crossings.EndCentre - reach);

		if (_Crossings.AtStart)
		{
			float before = _Crossings.StartCentre - reach;
			float after = _Crossings.StartCentre + reach;

			// A crossing at the very start just pushes the lines back; one further along cuts a gap in them.
			if (before <= start + 1.0f)
			{
				start = MathF.Max(start, after);
			}
			else
			{
				gapStart = before;
				gapEnd = after;
			}
		}

		return new LineRange(start, end, gapStart, gapEnd);
	}



	/// <summary>
	/// Which ends of a segment's borders nothing continues: a road end, a side of a junction with no corner on it,
	/// or a pass-through node where the road changes type or width.
	/// </summary>
	private BorderCaps ComputeBorderCaps(RoadSegment _Segment, Dictionary<int, RoadJunctionLayout> _Layouts)
	{
		var caps = BorderCaps.None;

		if (IsBorderOpen(_Segment, _Segment.StartNodeId, true, _Layouts, out bool startLeft, out bool startRight))
		{
			caps |= startLeft ? BorderCaps.StartLeft : BorderCaps.None;
			caps |= startRight ? BorderCaps.StartRight : BorderCaps.None;
		}

		if (IsBorderOpen(_Segment, _Segment.EndNodeId, false, _Layouts, out bool endLeft, out bool endRight))
		{
			caps |= endLeft ? BorderCaps.EndLeft : BorderCaps.None;
			caps |= endRight ? BorderCaps.EndRight : BorderCaps.None;
		}

		return caps;
	}



	/// <summary>
	/// A bridge segment's built range and ends. An end is closed off unless it is a pass-through node onto
	/// another bridge of the same style, width and border, where the two decks meet flush; there, only one of the
	/// two segments provides the node's railing post: the one leaving the node, or the lower id when both leave
	/// or both arrive.
	/// </summary>
	private RoadBridgeBuilder.BridgeSpan ComputeBridgeSpan(RoadSegment _Segment, float _RangeStart, float _RangeEnd)
	{
		var startNeighbour = DeckContinuation(_Segment, _Segment.StartNodeId);
		var endNeighbour = DeckContinuation(_Segment, _Segment.EndNodeId);

		bool postStart = startNeighbour is null || startNeighbour.EndNodeId == _Segment.StartNodeId || _Segment.Id < startNeighbour.Id;
		bool postEnd = endNeighbour is null || (endNeighbour.EndNodeId == _Segment.EndNodeId && _Segment.Id < endNeighbour.Id);

		return new RoadBridgeBuilder.BridgeSpan(_RangeStart, _RangeEnd, startNeighbour is null, postStart, endNeighbour is null, postEnd);
	}



	/// <summary>
	/// The bridge segment the deck carries straight on to at a node, or null where the deck ends.
	/// </summary>
	private RoadSegment DeckContinuation(RoadSegment _Segment, int _NodeId)
	{
		if (GetDegree(_NodeId) != 2)
			return null;

		var other = GetSegmentsAt(_NodeId).FirstOrDefault(candidate => candidate.Id != _Segment.Id);

		if (other?.Bridge is null || other.Bridge.ResourcePath != _Segment.Bridge.ResourcePath || !SameEdgesAt(_Segment, other, _NodeId))
			return null;

		bool sameExtent = MathF.Abs(RoadBridgeBuilder.DeckExtent(ProfileOf(other), other.Bridge) - RoadBridgeBuilder.DeckExtent(ProfileOf(_Segment), _Segment.Bridge)) < 0.01f;

		return sameExtent ? other : null;
	}



	/// <summary>
	/// The lift lines of a segment's two borders: at each end, the corner's height difference on that side (or
	/// nothing), run linearly along the road between the two.
	/// </summary>
	private RoadBorderBuilder.SegmentBorderBlends ComputeBorderBlends(RoadSegment _Segment, RoadCurveTable _Table, Dictionary<int, RoadJunctionLayout> _Layouts)
	{
		return new RoadBorderBuilder.SegmentBorderBlends(SideBlendFor(_Segment, _Table, true, _Layouts), SideBlendFor(_Segment, _Table, false, _Layouts));
	}



	private RoadBorderBuilder.SideBlend SideBlendFor(RoadSegment _Segment, RoadCurveTable _Table, bool _Left, Dictionary<int, RoadJunctionLayout> _Layouts)
	{
		bool hasStart = CornerLift(_Segment, _Segment.StartNodeId, true, _Left, _Layouts, out float startDistance, out float startDelta);
		bool hasEnd = CornerLift(_Segment, _Segment.EndNodeId, false, _Left, _Layouts, out float endDistance, out float endDelta);

		if (!hasStart && !hasEnd)
			return default;

		// An end with no corner on this side holds the border at road height from where the border actually starts,
		// which is past the trim: a road opening into a lot stops at the lot's flares, and a blend still running
		// there hands the lot a pavement tilted by whatever it had left to shed while the lot's own is flat.
		if (!hasStart)
		{
			startDistance = LotTrimAt(_Segment.StartNodeId);
			startDelta = 0.0f;
		}

		if (!hasEnd)
		{
			endDistance = _Table.Length - LotTrimAt(_Segment.EndNodeId);
			endDelta = 0.0f;
		}

		return new RoadBorderBuilder.SideBlend(true, startDistance, startDelta, endDistance, endDelta);
	}



	/// <summary>
	/// Where along the segment the corner's tangent point on this side is, and how much higher the corner sits
	/// than the road edge there; false when there is no inside corner on that side at that end.
	/// </summary>
	private static bool CornerLift(RoadSegment _Segment, int _NodeId, bool _AtStart, bool _Left, Dictionary<int, RoadJunctionLayout> _Layouts, out float _Distance, out float _Delta)
	{
		_Distance = 0.0f;
		_Delta = 0.0f;

		var fold = FindFold(_Segment, _NodeId, _AtStart, _Left, _Layouts, out bool sideA);

		if (fold is null || !_Layouts.TryGetValue(_NodeId, out var layout))
			return false;

		var arm = layout.FindArm(_Segment.Id);

		if (arm is null)
			return false;

		_Distance = arm.CurveDistance(sideA ? fold.TangentParamA : fold.TangentParamB);
		_Delta = fold.ApexHeight - (sideA ? fold.TangentHeightA : fold.TangentHeightB);

		return true;
	}



	private bool IsBorderOpen(RoadSegment _Segment, int _NodeId, bool _AtStart, Dictionary<int, RoadJunctionLayout> _Layouts, out bool _Left, out bool _Right)
	{
		int degree = GetDegree(_NodeId);

		// A road opening into a lot runs into the lot's own kerb, which closes it off; two caps in one place
		// would only fight each other.
		if (LotAtOpening(_NodeId) is { } lot && ProfileOf(lot).Border == ProfileOf(_Segment).Border)
		{
			_Left = false;
			_Right = false;
			return false;
		}

		if (degree <= 1)
		{
			// A road rounded off where it stops carries its border round the end; a cap across the road as well
			// would sit inside that.
			bool rounded = _Segment.Bridge is null && ProfileOf(_Segment).RoundedEnds;

			_Left = !rounded;
			_Right = !rounded;
			return !rounded;
		}

		if (_Layouts.TryGetValue(_NodeId, out var layout))
		{
			// A corner path starting on this arm continues its left border (looking out of the node); one ending on
			// it continues its right. Looking out of the node along a segment that ends there swaps the sides.
			bool armLeftContinued = layout.CornerPaths.Any(path => path.ArmA.Segment.Id == _Segment.Id);
			bool armRightContinued = layout.CornerPaths.Any(path => path.ArmB.Segment.Id == _Segment.Id);

			_Left = !(_AtStart ? armLeftContinued : armRightContinued);
			_Right = !(_AtStart ? armRightContinued : armLeftContinued);
			return _Left || _Right;
		}

		if (degree == 2)
		{
			var other = GetSegmentsAt(_NodeId).FirstOrDefault(candidate => candidate.Id != _Segment.Id);
			bool continuous = other is not null && SameEdgesAt(_Segment, other, _NodeId)
				&& (RoadBorderBuilder.SameProfile(ProfileOf(other), ProfileOf(_Segment)) || MeetsFlush(ProfileOf(other), ProfileOf(_Segment)));

			_Left = !continuous;
			_Right = !continuous;
			return !continuous;
		}

		_Left = true;
		_Right = true;
		return true;
	}



	/// <summary>
	/// Splits a segment into chunks at fixed distances from its start, clipped by the junction trims at each end,
	/// with a border chunk over each stretch when the road type has borders. Anchoring the boundaries to the
	/// segment rather than to the trimmed length means a junction change at one end only touches the chunk next
	/// to it.
	/// </summary>
	private List<ChunkPlan> PlanChunks(RoadSegment _Segment, RoadCurveTable _Table, float _TrimStart, float _TrimEnd, RoadProfile _Profile, BorderCaps _Caps, in RoadBorderBuilder.SegmentBorderBlends _Blends, in SegmentBorderTapers _Taper, long _SurfaceSignature, in LineRange _Lines, in RoadMarkingBuilder.DashPhase _Dash, in SegmentCrossings _Crossings, in RoadTrailFade _Trails, in RoadSurfaceBlend _SurfaceBlend)
	{
		float length = _Table.Length;
		int count = Math.Max(1, (int)MathF.Ceiling(length / ChunkLength));
		float usableEnd = length - _TrimEnd;
		var ranges = new List<(int Index, float Start, float End)>(count);

		for (int i = 0; i < count; i++)
		{
			float start = MathF.Max(length * i / count, _TrimStart);
			float end = MathF.Min(length * (i + 1) / count, usableEnd);

			if (end - start >= MinChunkLength)
				ranges.Add((i, start, end));
		}

		var plans = new List<ChunkPlan>(ranges.Count * 4);
		var borderHash = StableHash.Create();
		borderHash.Add(_SurfaceSignature);
		_Profile.AddBorderToHash(ref borderHash);
		_Blends.AddToHash(ref borderHash);
		_Taper.AddToHash(ref borderHash);
		borderHash.Add(DebugBorders ? 1 : 0);
		long borderSignature = borderHash.Value;

		// The deck follows the road and stands off its border, so it is stale when either changes; its posts and
		// supports are spread over the whole built range, so every piece is stale when an end moves.
		var bridge = _Segment.Bridge;
		var bridgeSpan = bridge is null ? default : ComputeBridgeSpan(_Segment, _TrimStart, usableEnd);
		long bridgeSignature = 0;

		if (bridge is not null)
		{
			var bridgeHash = StableHash.Create();
			bridgeHash.Add(_SurfaceSignature);
			bridgeHash.Add(bridge.ResourcePath);
			_Profile.AddBorderToHash(ref bridgeHash);
			bridge.AddToHash(ref bridgeHash);
			bridgeSpan.AddToHash(ref bridgeHash);
			bridgeSignature = bridgeHash.Value;
		}

		// Props are spread over the whole built range too, and stand on the border, so they follow its shape.
		var props = _Segment.Props;
		var propSpan = props is null ? default : new RoadPropBuilder.PropSpan(_TrimStart, usableEnd, GetDegree(_Segment.StartNodeId) >= 3, GetDegree(_Segment.EndNodeId) >= 3);
		long propSignature = 0;

		if (props is not null && !props.IsEmpty)
		{
			var propHash = StableHash.Create();
			propHash.Add(_SurfaceSignature);
			propHash.Add(props.ResourcePath);
			propHash.Add(bridge is null ? 0 : 1);
			_Profile.AddBorderToHash(ref propHash);
			props.AddToHash(ref propHash);
			propSpan.AddToHash(ref propHash);
			propSignature = propHash.Value;
		}

		// A road with no lines of its own still needs the piece when it has a crossing to paint.
		bool markings = RoadMarkingBuilder.HasMarkings(_Segment, _Profile) || _Crossings.AtStart || _Crossings.AtEnd;
		var markingHash = StableHash.Create();
		markingHash.Add(_SurfaceSignature);
		markingHash.Add(_Segment.LaneCount);
		markingHash.Add(_Segment.ForwardLanes);
		_Lines.AddToHash(ref markingHash);
		_Dash.AddToHash(ref markingHash);
		_Profile.AddMarkingsToHash(ref markingHash);
		_Profile.AddCrossingsToHash(ref markingHash);
		markingHash.Add(_Crossings.AtStart ? _Crossings.StartCentre : -1.0f);
		markingHash.Add(_Crossings.AtEnd ? _Crossings.EndCentre : -1.0f);
		long markingSignature = markingHash.Value;

		for (int r = 0; r < ranges.Count; r++)
		{
			var (index, start, end) = ranges[r];

			var hash = StableHash.Create();
			hash.Add(_SurfaceSignature);
			hash.Add(index);
			hash.Add(count);
			hash.Add(start);
			hash.Add(end);
			// Where the road climbs as it turns, its cells are cut into a column per lane (or more).
			hash.Add(_Segment.LaneCount);
			// Its trails rise out, and another surface blends into it, wherever the road stops, which moves with the
			// junctions at its ends.
			_Trails.AddToHash(ref hash);
			_SurfaceBlend.AddToHash(ref hash);

			plans.Add(new ChunkPlan(index, count, start, end, RoadChunkKind.Surface, BorderCaps.None, default, hash.Value, Trails: _Trails, Blend: _SurfaceBlend));

			if (markings)
			{
				hash = StableHash.Create();
				// A crossing belongs to one piece: the outermost, since its band may lie clear of every piece's
				// own stretch of road.
				var owned = new SegmentCrossings(_Crossings.AtStart && r == 0, _Crossings.StartCentre,
					_Crossings.AtEnd && r == ranges.Count - 1, _Crossings.EndCentre);

				hash.Add(markingSignature);
				hash.Add(index);
				hash.Add(count);
				hash.Add(start);
				hash.Add(end);
				hash.Add(owned.AtStart ? 1 : 0);
				hash.Add(owned.AtEnd ? 1 : 0);

				plans.Add(new ChunkPlan(index, count, start, end, RoadChunkKind.Marking, BorderCaps.None, default, hash.Value, _Lines, Dash: _Dash, Crossings: owned));
			}

			if (bridge is not null)
			{
				// The piece works out for itself whether it is an outermost one, from its stretch against the span.
				hash = StableHash.Create();
				hash.Add(bridgeSignature);
				hash.Add(index);
				hash.Add(count);
				hash.Add(start);
				hash.Add(end);

				plans.Add(new ChunkPlan(index, count, start, end, RoadChunkKind.Bridge, BorderCaps.None, default, hash.Value, Bridge: bridgeSpan));
			}

			if (propSignature != 0)
			{
				hash = StableHash.Create();
				hash.Add(propSignature);
				hash.Add(index);
				hash.Add(count);
				hash.Add(start);
				hash.Add(end);

				plans.Add(new ChunkPlan(index, count, start, end, RoadChunkKind.Props, BorderCaps.None, default, hash.Value, Props: propSpan));
			}

			if (!_Profile.HasBorder)
				continue;

			// Only the outermost stretches have open ends to close.
			var caps = BorderCaps.None;

			if (r == 0)
				caps |= _Caps & (BorderCaps.StartLeft | BorderCaps.StartRight);

			if (r == ranges.Count - 1)
				caps |= _Caps & (BorderCaps.EndLeft | BorderCaps.EndRight);

			hash = StableHash.Create();
			hash.Add(borderSignature);
			hash.Add(index);
			hash.Add(count);
			hash.Add(start);
			hash.Add(end);
			hash.Add((int)caps);

			plans.Add(new ChunkPlan(index, count, start, end, RoadChunkKind.Border, caps, _Blends, hash.Value, default, _Taper));
		}

		// A road that simply stops is rounded off there, in a piece of its own beyond the node.
		AddCapPlan(plans, _Segment, _Table, _Profile, _SurfaceSignature, true);
		AddCapPlan(plans, _Segment, _Table, _Profile, _SurfaceSignature, false);

		return plans;
	}



	/// <summary>
	/// The rounded end of a road, where it has one: at a node with nothing else at it, which is not a way into a
	/// parking lot (the lot's own kerb closes that), and on a road no bridge carries (a deck ends where it ends).
	/// The plan's start is the end's own distance along the curve and its end the depth the cap reaches past it.
	/// </summary>
	private void AddCapPlan(List<ChunkPlan> _Plans, RoadSegment _Segment, RoadCurveTable _Table, RoadProfile _Profile, long _SurfaceSignature, bool _AtStart)
	{
		int nodeId = _AtStart ? _Segment.StartNodeId : _Segment.EndNodeId;

		if (_Segment.Bridge is not null || GetDegree(nodeId) != 1 || LotAtOpening(nodeId) is not null)
			return;

		float distance = _AtStart ? 0.0f : _Table.Length;
		float depth = RoadCapBuilder.Depth(_Profile, new RoadWidth(_Segment, _Table.Length), distance);

		if (depth <= 0.01f)
			return;

		var hash = StableHash.Create();

		hash.Add(_SurfaceSignature);
		hash.Add(_AtStart ? 1 : 0);
		hash.Add(depth);
		_Profile.AddBorderToHash(ref hash);

		_Plans.Add(new ChunkPlan(_AtStart ? 0 : 1, 2, distance, depth, RoadChunkKind.Cap, BorderCaps.None, default, hash.Value));
	}



	private static long CombineSignatures(List<ChunkPlan> _Plans)
	{
		var hash = StableHash.Create();
		hash.Add(_Plans.Count);

		foreach (var plan in _Plans)
			hash.Add(plan.Signature);

		return hash.Value;
	}



	private bool IsSegmentUpToDate(List<RoadChunk> _Existing, List<ChunkPlan> _Plans)
	{
		int have = _Existing?.Count ?? 0;

		if (have != _Plans.Count)
			return false;

		foreach (var plan in _Plans)
		{
			var chunk = _Existing.FirstOrDefault(candidate => candidate.ChunkIndex == plan.Index && candidate.Kind == plan.Kind);

			if (chunk is null)
				return false;

			// A locked chunk is exempt: it is deliberately stale.
			if (!chunk.Locked && (chunk.Signature != plan.Signature || LacksDebugRecord(chunk)))
				return false;
		}

		return true;
	}



	/// <summary>
	/// While the border debug overlay is on, a border piece without its record (built before the flag went on, or
	/// before a hot reload, which drops the records) is rebuilt to get one.
	/// </summary>
	private bool LacksDebugRecord(RoadChunk _Chunk)
	{
		return DebugBorders && _Chunk.Kind == RoadChunkKind.Border && (_Chunk.Debug is null || _Chunk.Debug.Text.Length == 0);
	}



	/// <summary>
	/// Throws away every unlocked piece's signature so the next reconcile rebuilds all of them, junction layouts
	/// included. Toggling the GameObject does not do this: pieces whose inputs have not changed are kept as they
	/// are, so after a generator change this is the way to see it everywhere.
	/// </summary>
	[Button("Regenerate All", "refresh")]
	public void RegenerateAll()
	{
		foreach (var chunk in GetChunks())
		{
			if (!chunk.Locked)
				chunk.Signature = 0;
		}

		m_JunctionCache.Clear();
		MarkDirty();
	}



	private long ComputeSurfaceSignature(RoadSegment _Segment, in RoadCurve _Curve, RoadProfile _Profile)
	{
		var hash = StableHash.Create();

		hash.Add(GeneratorVersion);
		hash.Add(_Curve.P0);
		hash.Add(_Curve.P1);
		hash.Add(_Curve.P2);
		hash.Add(_Curve.P3);
		new RoadWidth(_Segment, 1.0f).AddToHash(ref hash);
		hash.Add(SampleSpacing);
		hash.Add(ChunkLength);
		hash.Add(SurfaceOffset);
		_Profile.AddSurfaceToHash(ref hash);

		return hash.Value;
	}



	/// <summary>
	/// Everything a node's junction layout depends on: the node itself and the curve and width of each road at it.
	/// </summary>
	private long ComputeJunctionKey(RoadNode _Node, float _Radius)
	{
		var hash = StableHash.Create();

		hash.Add(GeneratorVersion);
		hash.Add(_Node.Position);
		hash.Add(_Radius);
		hash.Add(SurfaceOffset);

		foreach (var segment in GetSegmentsAt(_Node.Id).OrderBy(segment => segment.Id))
		{
			hash.Add(segment.Id);
			new RoadWidth(segment, 1.0f).AddToHash(ref hash);
			hash.Add(segment.Ramp ? 1 : 0);
			hash.Add(segment.StartNodeId == _Node.Id ? 1 : 0);

			if (!TryGetCurve(segment, out var curve))
				continue;

			hash.Add(curve.P0);
			hash.Add(curve.P1);
			hash.Add(curve.P2);
			hash.Add(curve.P3);
		}

		return hash.Value;
	}



	private static void AddPolygonToHash(RoadJunctionLayout _Layout, ref StableHash _Hash)
	{
		_Hash.Add(GeneratorVersion);
		_Hash.Add(_Layout.Centre);
		_Hash.Add(_Layout.Polygon.Count);

		foreach (var vertex in _Layout.Polygon)
			_Hash.Add(vertex);
	}



	private long ComputeJunctionSurfaceSignature(RoadJunctionLayout _Layout, Dictionary<int, RoadSurfaceBlend> _ArmSurfaces)
	{
		var hash = StableHash.Create();

		AddPolygonToHash(_Layout, ref hash);
		JunctionSurfaceProfile(_Layout).AddSurfaceToHash(ref hash);

		// Each arm's texture runs on into the junction along the arm's own curve from the row it stops at, so the
		// junction moves with the curve, where along it that row is and which way it runs, and with whether the
		// road is surfaced the same; a road blending the junction's surface into its own starts doing so in its mouth.
		foreach (var arm in _Layout.Arms)
		{
			var curve = arm.Table.Curve;

			hash.Add(arm.Segment.Id);
			hash.Add(arm.AtStart ? 1 : 0);
			hash.Add(arm.CurveDistance(arm.Trim));
			hash.Add(curve.P0);
			hash.Add(curve.P1);
			hash.Add(curve.P2);
			hash.Add(curve.P3);
			ProfileOf(arm.Segment).AddSurfaceToHash(ref hash);
			_ArmSurfaces.GetValueOrDefault(arm.Segment.Id).AddToHash(ref hash);
		}

		return hash.Value;
	}



	private long ComputeJunctionBorderSignature(RoadJunctionLayout _Layout, Dictionary<int, RoadBorderBuilder.SegmentBorderBlends> _ArmBlends, Dictionary<int, SegmentBorderTapers> _ArmTapers)
	{
		var hash = StableHash.Create();

		AddPolygonToHash(_Layout, ref hash);
		hash.Add(DebugBorders ? 1 : 0);

		foreach (var arm in _Layout.Arms)
		{
			var profile = ProfileOf(arm.Segment);

			hash.Add(arm.Segment.Id);
			hash.Add(arm.AtStart ? 1 : 0);
			hash.Add(arm.Length);
			profile.AddSurfaceToHash(ref hash);
			profile.AddBorderToHash(ref hash);
			_ArmBlends.GetValueOrDefault(arm.Segment.Id).AddToHash(ref hash);
			_ArmTapers.GetValueOrDefault(arm.Segment.Id).AddToHash(ref hash);
		}

		return hash.Value;
	}



	/// <summary>
	/// A junction's surface takes after its main road: the widest arm.
	/// </summary>
	private RoadProfile JunctionSurfaceProfile(RoadJunctionLayout _Layout)
	{
		return ProfileOf(JunctionMainArm(_Layout)?.Segment);
	}



	private static RoadJunctionLayout.Arm JunctionMainArm(RoadJunctionLayout _Layout)
	{
		return _Layout.Arms.OrderByDescending(arm => arm.Widths.Base).ThenBy(arm => arm.Segment.Id).FirstOrDefault();
	}



	/// <summary>
	/// Whether an arm's road is surfaced like the junction it runs into (same material, laid the same way at the
	/// same scale), so the junction can carry the arm's texture on.
	/// </summary>
	private bool SameSurface(RoadJunctionLayout.Arm _Arm, RoadProfile _Surface)
	{
		return SameSurface(ProfileOf(_Arm.Segment), _Surface);
	}



	/// <summary>
	/// Whether two road types lay the same road surface: the same material, laid the same way at the same scale.
	/// </summary>
	private static bool SameSurface(RoadProfile _Profile, RoadProfile _Surface)
	{
		return _Profile == _Surface || (_Profile.ResolvedRoadMaterial?.ResourcePath == _Surface.ResolvedRoadMaterial?.ResourcePath
			&& _Profile.RoadTextureRepeat == _Surface.RoadTextureRepeat && _Profile.RoadTextureAcross == _Surface.RoadTextureAcross);
	}



	private void EnqueueSegment(RoadSegment _Segment, RoadCurveTable _Table, RoadProfile _Profile, List<ChunkPlan> _Plans, long _GroupSignature, List<RoadChunk> _Existing)
	{
		// Chunks with no plan (swallowed by a junction, past a shorter road's end, or a border the road no longer
		// has) have no job coming to replace them.
		if (_Existing is not null)
		{
			foreach (var chunk in _Existing)
			{
				if (!chunk.Locked && _Plans.All(plan => plan.Index != chunk.ChunkIndex || plan.Kind != chunk.Kind))
					chunk.GameObject.Destroy();
			}
		}

		if (_Plans.Count == 0)
		{
			m_PendingSegments.Remove(_Segment.Id);
			return;
		}

		foreach (var plan in _Plans)
			m_Jobs.Enqueue(new RebuildJob(_Segment.Id, -1, plan.Kind, _GroupSignature, plan, _Table, new RoadWidth(_Segment, _Table.Length), _Profile, null, null, null, null));

		m_PendingSegments[_Segment.Id] = new PendingGroup(_GroupSignature, _Plans.Count);
	}



	private void ProcessJobs()
	{
		// At least the set number of pieces, then more for as long as the frame's time allows: a drag changes a
		// handful of pieces every frame, and they should all follow it, not a few of them per frame.
		int count = Math.Max(1, RebuildBudget);
		double time = Math.Max(0.0f, RebuildTimeBudget);
		var clock = System.Diagnostics.Stopwatch.StartNew();

		while (m_Jobs.Count > 0 && (count > 0 || clock.Elapsed.TotalMilliseconds < time))
		{
			var job = m_Jobs.Dequeue();

			// Superseded by a newer version of the same piece, or its road, junction or lot is gone.
			if (!IsCurrent(job))
				continue;

			bool built;

			if (job.Lot is not null)
			{
				BuildLotPiece(job);
				built = true;

				m_PendingLots.Remove((job.Lot.Id, job.Kind));
			}
			else if (job.NodeId >= 0)
			{
				BuildJunctionPiece(job);
				built = true;

				m_PendingJunctions.Remove((job.NodeId, job.Kind));
			}
			else
			{
				var pendingSegment = m_PendingSegments[job.SegmentId];

				built = BuildChunk(job);

				if (pendingSegment.Remaining <= 1)
					m_PendingSegments.Remove(job.SegmentId);
				else
					m_PendingSegments[job.SegmentId] = pendingSegment with { Remaining = pendingSegment.Remaining - 1 };
			}

			if (!built)
				continue;

			count--;
			m_PieceStamps[KeyOf(job)] = m_TickCount;
		}

		if (m_Jobs.Count == 0)
			m_PieceStamps.Clear();
	}



	/// <summary>
	/// Returns false when there was nothing to do: the chunk in place was already built from these inputs (a
	/// junction change at the far end of the road requeues the whole road, but only the chunk next to it differs).
	/// </summary>
	private bool BuildChunk(in RebuildJob _Job)
	{
		var plan = _Job.Plan;
		var existing = FindChunk(_Job.SegmentId, plan.Index, plan.Kind);

		if (existing is not null && (existing.Locked || (existing.Signature == plan.Signature && !LacksDebugRecord(existing))))
			return false;

		PolygonMesh mesh;
		string name;
		BorderDebug debug = null;
		var placements = new List<(GameObject Prefab, Transform Transform)>();

		if (plan.Kind == RoadChunkKind.Border)
		{
			debug = DebugBorders ? new BorderDebug() : null;
			mesh = RoadBorderBuilder.BuildSegment(_Job.Table, plan.Start, plan.End, _Job.Width, _Job.Profile, SampleSpacing, SurfaceOffset, plan.Caps, plan.Blends, plan.Taper, debug);
			name = $"Road {_Job.SegmentId} border [{plan.Index + 1}/{plan.Count}]";
		}
		else if (plan.Kind == RoadChunkKind.Bridge)
		{
			var segment = FindSegment(_Job.SegmentId);

			if (segment?.Bridge is null)
				return false;

			var parts = new List<RoadBridgeBuilder.Placement>();
			mesh = RoadBridgeBuilder.Build(_Job.Table, plan.Start, plan.End, plan.Bridge, _Job.Width, _Job.Profile, segment.Bridge, SampleSpacing, SurfaceOffset, GroundBelow, parts);
			name = $"Road {_Job.SegmentId} bridge [{plan.Index + 1}/{plan.Count}]";

			foreach (var part in parts)
				placements.Add((part.Prefab, new Transform(part.Position, part.Rotation)));
		}
		else if (plan.Kind == RoadChunkKind.Props)
		{
			var segment = FindSegment(_Job.SegmentId);

			if (segment?.Props is null)
				return false;

			// Nothing to mesh: the piece is only the parent of its prefabs.
			var props = new List<RoadPropBuilder.Placement>();
			var context = new RoadPropBuilder.Context(_Job.Table, plan.Start, plan.End, plan.Props, _Job.SegmentId, _Job.Width, _Job.Profile, segment.Bridge is not null, SurfaceOffset, GroundAround);

			RoadPropBuilder.Build(context, segment.Props, props);
			mesh = null;
			name = $"Road {_Job.SegmentId} props [{plan.Index + 1}/{plan.Count}]";

			foreach (var prop in props)
				placements.Add((prop.Prefab, new Transform(prop.Position, prop.Rotation, prop.Scale)));
		}
		else if (plan.Kind == RoadChunkKind.Cap)
		{
			// Surface and border in one piece: the whole end is small, and nothing else shares it.
			mesh = RoadCapBuilder.Build(_Job.Table, plan.Start, plan.Index == 0, _Job.Width, _Job.Profile, SurfaceOffset, plan.End);
			name = $"Road {_Job.SegmentId} {(plan.Index == 0 ? "start" : "end")} cap";
		}
		else if (plan.Kind == RoadChunkKind.Marking)
		{
			var segment = FindSegment(_Job.SegmentId);

			if (segment is null)
				return false;

			// Lines only where the chunk overlaps the stretches left clear by the crossings, laid on the rows the
			// road surface itself was built on.
			mesh = new PolygonMesh();

			var roadRows = RoadRows.Build(_Job.Table, plan.Start, plan.End, SampleSpacing, _Job.Width, _Job.Profile.Simplification);
			int columns = RoadSurfaceBuilder.Columns(_Job.Table, roadRows, _Job.Width, segment.LaneCount);

			if (plan.Lines.HasGap)
			{
				RoadMarkingBuilder.Build(mesh, _Job.Table, roadRows, columns, MathF.Max(plan.Start, plan.Lines.Start), MathF.Min(plan.End, plan.Lines.GapStart), segment, _Job.Width, _Job.Profile, SurfaceOffset, plan.Dash);
				RoadMarkingBuilder.Build(mesh, _Job.Table, roadRows, columns, MathF.Max(plan.Start, plan.Lines.GapEnd), MathF.Min(plan.End, plan.Lines.End), segment, _Job.Width, _Job.Profile, SurfaceOffset, plan.Dash);
			}
			else
			{
				RoadMarkingBuilder.Build(mesh, _Job.Table, roadRows, columns, MathF.Max(plan.Start, plan.Lines.Start), MathF.Min(plan.End, plan.Lines.End), segment, _Job.Width, _Job.Profile, SurfaceOffset, plan.Dash);
			}

			// The crossings this piece owns, laid on the same rows as the lines and the road itself.
			if (plan.Crossings.AtStart)
				RoadMarkingBuilder.BuildCrossing(mesh, _Job.Table, roadRows, columns, plan.Crossings.StartCentre, _Job.Width, _Job.Profile, SurfaceOffset);

			if (plan.Crossings.AtEnd)
				RoadMarkingBuilder.BuildCrossing(mesh, _Job.Table, roadRows, columns, plan.Crossings.EndCentre, _Job.Width, _Job.Profile, SurfaceOffset);

			name = $"Road {_Job.SegmentId} markings [{plan.Index + 1}/{plan.Count}]";
		}
		else
		{
			var segment = FindSegment(_Job.SegmentId);

			if (segment is null)
				return false;

			mesh = RoadSurfaceBuilder.Build(_Job.Table, plan.Start, plan.End, _Job.Width, SampleSpacing, _Job.Profile.Simplification, segment.LaneCount, _Job.Profile.RoadTextureRepeat, _Job.Profile.RoadTextureFitsWidth, SurfaceOffset, _Job.Profile.ResolvedRoadMaterial, _Job.Profile, plan.Trails, plan.Blend);
			name = $"Road {_Job.SegmentId} [{plan.Index + 1}/{plan.Count}]";
		}

		var piece = CreateChunkObject(name, _Job.SegmentId, -1, plan.Kind, plan.Index, plan.Count, plan.Signature, mesh, debug);

		foreach (var (prefab, transform) in placements)
			prefab.Clone(transform, piece);

		// The new piece is in place before the old one goes, so the road never flickers.
		existing?.GameObject.Destroy();

		return true;
	}



	/// <summary>
	/// The height of whatever lies under a point of the network (its own pieces excepted), in network space, for
	/// bridge supports to land on: the search starts just below the point, so the deck itself is never it. Null
	/// over nothing.
	/// </summary>
	private float? GroundBelow(Vector3 _Local)
	{
		return TraceGround(_Local, -1.0f);
	}



	/// <summary>
	/// The height of the ground at a point of the network, for props to stand on: the search starts well above
	/// the point, so ground rising beside a road is found as well as ground falling away from it.
	/// </summary>
	private float? GroundAround(Vector3 _Local)
	{
		return TraceGround(_Local, 3000.0f);
	}



	private float? TraceGround(Vector3 _Local, float _StartAbove)
	{
		const float reach = 50000.0f;

		var transform = WorldTransform;
		Vector3 from = transform.PointToWorld(_Local) + Vector3.Up * _StartAbove;
		var trace = Scene.Trace.Ray(from, from + Vector3.Down * reach).WithoutTags(ChunkTag).Run();

		if (!trace.Hit)
			return null;

		return transform.PointToLocal(trace.EndPosition).z;
	}



	private void BuildJunctionPiece(in RebuildJob _Job)
	{
		var existing = FindJunctionChunk(_Job.NodeId, _Job.Kind);

		if (existing is not null && existing.Locked)
			return;

		PolygonMesh mesh;
		string name;
		BorderDebug debug = null;

		if (_Job.Kind == RoadChunkKind.Border)
		{
			var armBlends = _Job.ArmBlends;
			var armTapers = _Job.ArmTapers;

			debug = DebugBorders ? new BorderDebug() : null;
			mesh = RoadBorderBuilder.BuildJunction(_Job.Junction, ProfileOf,
				segment => armBlends is not null ? armBlends.GetValueOrDefault(segment.Id) : default,
				segment => armTapers is not null ? armTapers.GetValueOrDefault(segment.Id) : default,
				debug);
			name = $"Junction {_Job.NodeId} border";
		}
		else
		{
			var profile = JunctionSurfaceProfile(_Job.Junction);
			var surfaces = _Job.ArmSurfaces;

			mesh = RoadJunctionBuilder.Build(_Job.Junction, JunctionMainArm(_Job.Junction), arm => SameSurface(arm, profile), profile.RoadTextureRepeat, profile.RoadTextureFitsWidth, profile.ResolvedRoadMaterial, arm => ProfileOf(arm.Segment),
				arm => surfaces is not null ? surfaces.GetValueOrDefault(arm.Segment.Id) : default);
			name = $"Junction {_Job.NodeId}";
		}

		CreateChunkObject(name, -1, _Job.NodeId, _Job.Kind, 0, 1, _Job.GroupSignature, mesh, debug);

		existing?.GameObject.Destroy();
	}



	private GameObject CreateChunkObject(string _Name, int _SegmentId, int _NodeId, RoadChunkKind _Kind, int _Index, int _Count, long _Signature, PolygonMesh _Mesh, BorderDebug _Debug, int _LotId = -1)
	{
		var child = new GameObject(GameObject, true, _Name);
		child.Tags.Add(ChunkTag);

		var chunk = child.AddComponent<RoadChunk>();
		chunk.SegmentId = _SegmentId;
		chunk.NodeId = _NodeId;
		chunk.LotId = _LotId;
		chunk.Kind = _Kind;
		chunk.ChunkIndex = _Index;
		chunk.ChunkCount = _Count;
		chunk.Signature = _Signature;
		chunk.Debug = _Debug;

		if (_Debug is not null)
			DebugRevision++;

		// A piece of props has no geometry of its own; it is the parent of its prefabs.
		if (_Mesh is null)
			return child;

		var meshComponent = child.AddComponent<MeshComponent>();
		meshComponent.Mesh = _Mesh;
		meshComponent.SmoothingAngle = 40.0f;

		// Paint lies flat on the road: nothing to collide with, and its shadow would be a sliver hidden under it,
		// at the cost of a shadow pass.
		if (_Kind == RoadChunkKind.Marking)
		{
			meshComponent.Collision = MeshComponent.CollisionType.None;
			meshComponent.RenderType = ModelRenderer.ShadowRenderType.Off;
		}

		return child;
	}
}
