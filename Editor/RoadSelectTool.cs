using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;

namespace RedSnail.RoadTool2.Editor;

/// <summary>
/// Which frame a node's position is shown and edited in.
/// </summary>
public enum RoadNodeSpace
{
	/// <summary>
	/// The network's own axes.
	/// </summary>
	Global,

	/// <summary>
	/// The node's road: X along it, Y across it. Height stays height either way.
	/// </summary>
	Local
}

/// <summary>
/// Select and edit what is already there: click a node or a road to select it, drag a node along the ground (or use
/// its move gizmo), drag a node's tangent handles to reshape the roads through it, Ctrl+click a road to insert a
/// node, Delete to remove the selection.
/// </summary>
[Title("Edit")]
[Icon("edit")]
[Group("1")]
[Order(1)]
public class RoadSelectTool : EditorTool
{
	private enum DragKind
	{
		None,
		Node,
		Handle,
		Gizmo
	}

	private const float MinEndDistance = 25.0f;

	private readonly List<int> m_NodeIds = [];

	/// <summary>
	/// The selected node when exactly one is selected, otherwise -1. The move gizmo, the tangent handles and the
	/// position fields only work on a single node.
	/// </summary>
	public int SelectedNodeId => m_NodeIds.Count == 1 ? m_NodeIds[0] : -1;

	/// <summary>
	/// Every selected node. Shift+click adds a node to the selection or takes it out again. Nodes and roads are
	/// never selected together.
	/// </summary>
	public IReadOnlyList<int> SelectedNodeIds => m_NodeIds;

	private readonly List<int> m_SegmentIds = [];

	/// <summary>
	/// Every selected road. Shift+click adds a road to the selection or takes it out again.
	/// </summary>
	public IReadOnlyList<int> SelectedSegmentIds => m_SegmentIds;

	/// <summary>
	/// The selected road when exactly one is selected, otherwise -1.
	/// </summary>
	public int SelectedSegmentId => m_SegmentIds.Count == 1 ? m_SegmentIds[0] : -1;

	/// <summary>
	/// Bumped whenever the selection changes, so the sidebar knows to rebuild itself.
	/// </summary>
	public int SelectionVersion { get; private set; }

	public RoadNetwork Network => Root?.Network;

	public IEnumerable<RoadNode> SelectedNodes => Network.IsValid()
		? m_NodeIds.Select(Network.FindNode).Where(node => node is not null)
		: [];

	public RoadNode SelectedNode => Network.IsValid() ? Network.FindNode(SelectedNodeId) : null;

	public RoadSegment SelectedSegment => Network.IsValid() ? Network.FindSegment(SelectedSegmentId) : null;

	/// <summary>
	/// Every selected road that still exists.
	/// </summary>
	public IEnumerable<RoadSegment> SelectedSegments => Network.IsValid()
		? m_SegmentIds.Select(Network.FindSegment).Where(segment => segment is not null)
		: [];

	/// <summary>
	/// Which frame the selected node's position is shown and edited in, the same choice the move gizmo offers.
	/// Local puts X along the road through the node and Y across it, so a value typed there slides the node down
	/// its own road rather than along a world axis.
	/// </summary>
	[Property, Title("Space")] public RoadNodeSpace NodeSpace { get; set; } = RoadNodeSpace.Global;

	/// <summary>
	/// The selected node's position, in whichever frame <see cref="NodeSpace"/> asks for.
	/// </summary>
	[Property, Title("Position")]
	public Vector3 NodePosition
	{
		get
		{
			var network = Network;

			if (!network.IsValid() || SelectedNode is not { } node)
				return Vector3.Zero;

			return NodeSpace == RoadNodeSpace.Local ? NodeAxes(network, node).Inverse * node.Position : node.Position;
		}
		set
		{
			var network = Network;

			if (!network.IsValid() || SelectedNode is not { } node)
				return;

			network.MoveNode(node.Id, NodeSpace == RoadNodeSpace.Local ? NodeAxes(network, node) * value : value);
		}
	}

	private RoadNetworkTool Root => Manager?.CurrentTool as RoadNetworkTool;



	/// <summary>
	/// The node's own axes in the network's space: forward along the road through it, laid flat so that Z stays
	/// height and a number typed into it does not drag the node along the road's grade. World axes when no road at
	/// the node gives a direction.
	/// </summary>
	private static Rotation NodeAxes(RoadNetwork _Network, RoadNode _Node)
	{
		var segment = _Network.GetSegmentsAt(_Node.Id).FirstOrDefault();

		if (segment is null)
			return Rotation.Identity;

		// The handle points from the node into its road: at the road's end that is against the road's direction.
		Vector3 handle = RoadNetwork.GetHandle(segment, _Node.Id);
		Vector3 forward = (segment.EndNodeId == _Node.Id ? -handle : handle).WithZ(0.0f);

		return forward.IsNearZeroLength ? Rotation.Identity : Rotation.LookAt(forward.Normal, Vector3.Up);
	}

	private DragKind m_Drag;
	private int m_DragSegmentId = -1;
	private Vector3 m_DragStartWorld;
	private Vector3 m_MoveDelta;
	private IDisposable m_UndoScope;

	// Shift+click then drag with the button held: what was under the first click (a node or a road, added or taken out)
	// is done to everything of that kind the cursor passes over.
	private bool m_Painting;
	private bool m_PaintNodes;
	private bool m_PaintAdd;



	public override void OnDisabled()
	{
		m_Painting = false;
		EndDrag();
	}



	public override Widget CreateToolSidebar()
	{
		return new RoadSelectToolWidget(this);
	}



	public override void OnUpdate()
	{
		var network = Network;

		if (!network.IsValid())
		{
			EndDrag();
			ClearSelection();
			return;
		}

		// Undo can remove whatever we had selected.
		if (m_NodeIds.RemoveAll(id => network.FindNode(id) is null) > 0)
			SelectionVersion++;

		if (m_SegmentIds.RemoveAll(id => network.FindSegment(id) is null) > 0)
			SelectionVersion++;

		if (m_Drag is DragKind.Node or DragKind.Handle)
		{
			UpdateDirectDrag(network);
			DrawSelection(network);
			return;
		}

		if (m_Painting)
		{
			if (Gizmo.IsLeftMouseDown)
			{
				PaintSelect(network);
				DrawSelection(network);
				return;
			}

			m_Painting = false;
		}

		bool canPick = Gizmo.HasMouseFocus && !Gizmo.HasHovered && !Gizmo.Pressed.Any;
		RoadNode hoverNode = null;
		RoadSegmentPick? hoverSegment = null;
		int hoverHandleSegment = -1;

		if (canPick)
		{
			var ray = Gizmo.CurrentRay;

			if (SelectedNode is { } selected)
				hoverHandleSegment = PickHandle(network, selected, ray);

			if (hoverHandleSegment < 0)
				hoverNode = RoadPicker.PickNode(network, ray);

			if (hoverHandleSegment < 0 && hoverNode is null)
				hoverSegment = RoadPicker.PickSegment(network, ray);
		}

		DrawHover(network, hoverNode, hoverSegment);
		DrawSelection(network, hoverHandleSegment);

		if (canPick && Gizmo.WasLeftMousePressed)
			OnPress(network, hoverNode, hoverSegment, hoverHandleSegment);

		if (SelectedNode is { } node)
			UpdateNodeGizmo(network, node);
	}



	public void Select(RoadNode _Node)
	{
		SetSelection(_Node?.Id ?? -1, -1);
	}



	public void Select(RoadSegment _Segment)
	{
		SetSelection(-1, _Segment?.Id ?? -1);
	}



	/// <summary>
	/// Adds the node to the selection, or takes it out if it is already in, and says which it did. Selected roads
	/// are dropped first.
	/// </summary>
	public bool ToggleSelect(RoadNode _Node)
	{
		if (_Node is null)
			return false;

		m_SegmentIds.Clear();

		bool added = !m_NodeIds.Remove(_Node.Id);

		if (added)
			m_NodeIds.Add(_Node.Id);

		SelectionVersion++;

		return added;
	}



	/// <summary>
	/// Adds the road to the selection, or takes it out if it is already in, and says which it did. Selected nodes
	/// are dropped first.
	/// </summary>
	public bool ToggleSelect(RoadSegment _Segment)
	{
		if (_Segment is null)
			return false;

		m_NodeIds.Clear();

		bool added = !m_SegmentIds.Remove(_Segment.Id);

		if (added)
			m_SegmentIds.Add(_Segment.Id);

		SelectionVersion++;

		return added;
	}



	public void ClearSelection()
	{
		SetSelection(-1, -1);
	}



	private void SetSelection(int _NodeId, int _SegmentId)
	{
		if (m_NodeIds.SequenceEqual(_NodeId >= 0 ? [_NodeId] : []) && m_SegmentIds.SequenceEqual(_SegmentId >= 0 ? [_SegmentId] : []))
			return;

		m_NodeIds.Clear();
		m_SegmentIds.Clear();

		if (_NodeId >= 0)
			m_NodeIds.Add(_NodeId);

		if (_SegmentId >= 0)
			m_SegmentIds.Add(_SegmentId);

		SelectionVersion++;
	}



	public void DeleteSelection()
	{
		var network = Network;

		if (!network.IsValid())
			return;

		if (m_NodeIds.Count > 0)
		{
			using (SceneEditorSession.Active.UndoScope(m_NodeIds.Count > 1 ? "Delete Nodes" : "Delete Node").WithComponentChanges(network).Push())
			{
				foreach (int id in m_NodeIds.ToArray())
				{
					// An earlier removal can have taken this node's roads and the node with them.
					if (network.FindNode(id) is null)
						continue;

					// A node in the middle of a road is dissolved so the road carries on in one piece; anything else
					// (a road end, a junction) takes its roads with it.
					if (network.GetDegree(id) != 2 || network.DissolveNode(id) is null)
						network.RemoveNode(id);
				}
			}
		}
		else if (m_SegmentIds.Count > 0)
		{
			using (SceneEditorSession.Active.UndoScope(m_SegmentIds.Count > 1 ? "Delete Roads" : "Delete Road").WithComponentChanges(network).Push())
			{
				foreach (int id in m_SegmentIds.ToArray())
					network.RemoveSegment(id);
			}
		}

		ClearSelection();
	}



	/// <summary>
	/// Sidebar edits of the selected node land on the node object directly; this pushes them through the network
	/// so tangents and chunks follow.
	/// </summary>
	public void OnNodeEdited(RoadNode _Node, string _PropertyName)
	{
		var network = Network;

		if (!network.IsValid() || _Node is null)
			return;

		// The value is already written on the node; only the consequences are left to apply.
		if (_PropertyName == nameof(RoadNode.Position))
			network.RefreshTangentsAround(_Node.Id);

		network.MarkDirty();
	}



	public void OnSegmentEdited()
	{
		var network = Network;

		if (!network.IsValid())
			return;

		// Turning a road into a ramp (or back) changes how the roads at its ends flow through their nodes.
		foreach (var segment in SelectedSegments.ToArray())
		{
			network.RefreshTangentsAround(segment.StartNodeId);
			network.RefreshTangentsAround(segment.EndNodeId);
		}

		network.MarkDirty();
	}



	/// <summary>
	/// While a Shift+click is held and dragged: add what is under the cursor to the selection, or take it out, the way
	/// the first click did.
	/// </summary>
	private void PaintSelect(RoadNetwork _Network)
	{
		var ray = Gizmo.CurrentRay;

		if (m_PaintNodes)
		{
			if (RoadPicker.PickNode(_Network, ray) is { } node && m_NodeIds.Contains(node.Id) != m_PaintAdd)
				ToggleSelect(node);
		}
		else if (RoadPicker.PickSegment(_Network, ray) is { } pick && m_SegmentIds.Contains(pick.Segment.Id) != m_PaintAdd)
		{
			ToggleSelect(pick.Segment);
		}
	}



	private void OnPress(RoadNetwork _Network, RoadNode _HoverNode, RoadSegmentPick? _HoverSegment, int _HoverHandleSegment)
	{
		if (_HoverHandleSegment >= 0)
		{
			m_Drag = DragKind.Handle;
			m_DragSegmentId = _HoverHandleSegment;

			if (SelectedNode is { } handleNode && _Network.FindSegment(_HoverHandleSegment) is { } handleSegment)
				m_DragStartWorld = _Network.WorldTransform.PointToWorld(handleNode.Position + RoadNetwork.GetHandle(handleSegment, handleNode.Id));

			return;
		}

		// Shift+click on a road adds it to the selection or takes it out; a click elsewhere leaves the selection alone.
		if (Gizmo.IsShiftPressed)
		{
			if (_HoverNode is not null)
			{
				m_PaintNodes = true;
				m_PaintAdd = ToggleSelect(_HoverNode);
				m_Painting = true;
			}
			else if (_HoverSegment is { } shiftPick)
			{
				m_PaintNodes = false;
				m_PaintAdd = ToggleSelect(shiftPick.Segment);
				m_Painting = true;
			}

			return;
		}

		if (_HoverNode is not null)
		{
			Select(_HoverNode);
			m_Drag = DragKind.Node;
			m_DragStartWorld = _Network.WorldTransform.PointToWorld(_HoverNode.Position);
			return;
		}

		if (_HoverSegment is { } pick)
		{
			if (Gizmo.IsCtrlPressed)
			{
				RoadNode inserted;

				using (SceneEditorSession.Active.UndoScope("Insert Node").WithComponentChanges(_Network).Push())
					inserted = _Network.SplitSegment(pick.Segment.Id, pick.T, MinEndDistance);

				Select(inserted);
			}
			else
			{
				Select(pick.Segment);
			}

			return;
		}

		ClearSelection();
	}



	/// <summary>
	/// A node pressed directly slides along the ground under the cursor; a handle slides on the horizontal plane
	/// through where it was picked up, so a road's vertical profile is only changed on purpose (through the gizmo).
	/// The plane stays put while the handle moves: a node fitting its handles' pitch lifts and lowers the tip as it
	/// goes, and a plane following the tip would chase it.
	/// </summary>
	private void UpdateDirectDrag(RoadNetwork _Network)
	{
		if (!Gizmo.IsLeftMouseDown)
		{
			EndDrag();
			return;
		}

		// A press that never travels is a click, not a drag: don't record an undo step for it.
		if (Vector2.Distance(Gizmo.CursorPosition, Gizmo.Pressed.CursorPosition) < 2.0f && m_UndoScope is null)
			return;

		var transform = _Network.WorldTransform;

		if (m_Drag == DragKind.Node && SelectedNode is { } node)
		{
			if (GroundHit(_Network, transform.PointToWorld(node.Position)) is not Vector3 hit)
				return;

			m_UndoScope ??= SceneEditorSession.Active.UndoScope("Move Node").WithComponentChanges(_Network).Push();
			_Network.MoveNode(node.Id, transform.PointToLocal(hit));
		}
		else if (m_Drag == DragKind.Handle && SelectedNode is { } handleNode && _Network.FindSegment(m_DragSegmentId) is { } segment)
		{
			var plane = new Plane(m_DragStartWorld, Vector3.Up);

			if (plane.Trace(Gizmo.CurrentRay, true) is not Vector3 hit)
				return;

			m_UndoScope ??= SceneEditorSession.Active.UndoScope("Move Tangent").WithComponentChanges(_Network).Push();

			// Alt breaks the mirror so a pass-through node can be turned into a corner.
			Vector3 handle = transform.PointToLocal(hit) - handleNode.Position;
			_Network.SetHandle(segment.Id, handleNode.Id, handle, !Gizmo.IsAltPressed);
		}
		else
		{
			EndDrag();
		}
	}



	/// <summary>
	/// The standard move gizmo on the selected node. Its centre square drags along the ground (the raycast hook),
	/// the arrows move on world axes, or in Local space (the editor's Global/Local toggle) along and across the
	/// road through the node, with grid snapping in whichever frame the arrows are in.
	/// </summary>
	private void UpdateNodeGizmo(RoadNetwork _Network, RoadNode _Node)
	{
		if (m_Drag is DragKind.Node or DragKind.Handle)
			return;

		var transform = _Network.WorldTransform;
		Vector3 nodeWorld = transform.PointToWorld(_Node.Position);
		var handleRotation = Gizmo.Settings.GlobalSpace ? Rotation.Identity : NodeFrame(_Network, _Node);

		using (Gizmo.Scope("roadtool2_node_gizmo", new Transform(nodeWorld)))
		{
			Gizmo.Hitbox.DepthBias = 0.01f;

			Vector3? surfaceHit = null;

			bool moved = Gizmo.Control.Position("position", Vector3.Zero, out Vector3 delta, handleRotation, centerRaycast: () =>
			{
				surfaceHit = GroundHit(_Network, nodeWorld);
				return surfaceHit;
			});

			if (moved)
			{
				if (m_Drag != DragKind.Gizmo)
				{
					m_Drag = DragKind.Gizmo;
					m_DragStartWorld = nodeWorld;
					m_MoveDelta = Vector3.Zero;
					m_UndoScope ??= SceneEditorSession.Active.UndoScope("Move Node").WithComponentChanges(_Network).Push();
				}

				Vector3 target;

				if (surfaceHit is Vector3 hit)
				{
					target = hit;
				}
				else
				{
					m_MoveDelta += delta;
					target = Gizmo.Snap(m_DragStartWorld, m_MoveDelta, handleRotation);
				}

				_Network.MoveNode(_Node.Id, transform.PointToLocal(target));
			}
		}

		if (m_Drag == DragKind.Gizmo && !Gizmo.IsLeftMouseDown)
			EndDrag();
	}



	/// <summary>
	/// The node's own frame for Local space: forward is the way the road through it runs (its first road's
	/// direction, which at a smooth pass-through node is the same from either side), so the arrows point along
	/// and across the road. World axes when no road at the node gives a direction.
	/// </summary>
	private static Rotation NodeFrame(RoadNetwork _Network, RoadNode _Node)
	{
		var segment = _Network.GetSegmentsAt(_Node.Id).FirstOrDefault();

		if (segment is null)
			return Rotation.Identity;

		// The handle points from the node into its road: at the road's end that is against the road's direction.
		Vector3 handle = RoadNetwork.GetHandle(segment, _Node.Id);
		Vector3 forward = _Network.WorldTransform.NormalToWorld(segment.EndNodeId == _Node.Id ? -handle : handle);

		if (forward.IsNearZeroLength || MathF.Abs(forward.Normal.z) > 0.99f)
			return Rotation.Identity;

		return Rotation.LookAt(forward.Normal, Vector3.Up);
	}



	private void EndDrag()
	{
		m_Drag = DragKind.None;
		m_DragSegmentId = -1;
		m_UndoScope?.Dispose();
		m_UndoScope = null;
	}



	/// <summary>
	/// Where the cursor ray meets the ground, ignoring the road meshes themselves (the node sits right on them), and
	/// on a terrain the ground the roads are laid over rather than the terrain as they shaped it. Falls back to a
	/// horizontal plane through the point being dragged when nothing is under the cursor.
	/// </summary>
	private Vector3? GroundHit(RoadNetwork _Network, Vector3 _Fallback)
	{
		var trace = Trace.WithoutTags(RoadNetwork.ChunkTag).Run();

		if (trace.Hit)
			return RoadTerrainLayers.OntoGround(trace, Gizmo.CurrentRay);

		return new Plane(_Fallback, Vector3.Up).Trace(Gizmo.CurrentRay, true);
	}



	/// <summary>
	/// The segment whose tangent handle at the selected node is under the ray, or -1.
	/// </summary>
	private static int PickHandle(RoadNetwork _Network, RoadNode _Node, Ray _Ray)
	{
		var transform = _Network.WorldTransform;
		int best = -1;
		float bestDistance = float.MaxValue;

		foreach (var segment in _Network.GetSegmentsAt(_Node.Id))
		{
			Vector3 world = transform.PointToWorld(_Node.Position + RoadNetwork.GetHandle(segment, _Node.Id));
			var sphere = new Sphere(world, RoadPicker.NodeRadius(world) * 0.7f);

			if (!sphere.Trace(_Ray, float.MaxValue, out float distance) || distance >= bestDistance)
				continue;

			best = segment.Id;
			bestDistance = distance;
		}

		return best;
	}



	private static void DrawHover(RoadNetwork _Network, RoadNode _HoverNode, RoadSegmentPick? _HoverSegment)
	{
		var transform = _Network.WorldTransform;

		using (Gizmo.Scope("roadtool2_hover"))
		{
			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.LineThickness = 2.0f;
			Gizmo.Draw.Color = Color.Cyan.WithAlpha(0.8f);

			if (_HoverNode is not null)
				RoadGizmos.DrawNode(transform.PointToWorld(_HoverNode.Position), false);

			if (_HoverSegment is { } pick && _Network.TryGetCurve(pick.Segment, out var curve))
			{
				RoadGizmos.DrawRoadOutline(curve, _Network.WidthOf(pick.Segment), transform);

				// Ctrl turns the click into "insert a node here": show where.
				if (Gizmo.IsCtrlPressed)
				{
					Gizmo.Draw.Color = Color.Orange;
					RoadGizmos.DrawNode(transform.PointToWorld(pick.LocalPoint), true);
				}
			}
		}
	}



	private void DrawSelection(RoadNetwork _Network, int _HoverHandleSegment = -1)
	{
		var transform = _Network.WorldTransform;

		using (Gizmo.Scope("roadtool2_selection"))
		{
			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.LineThickness = 2.0f;

			foreach (var segment in SelectedSegments)
			{
				if (!_Network.TryGetCurve(segment, out var curve))
					continue;

				Gizmo.Draw.Color = Color.Yellow;
				RoadGizmos.DrawRoadOutline(curve, _Network.WidthOf(segment), transform);
			}

			if (m_NodeIds.Count > 1)
			{
				Gizmo.Draw.Color = Color.Yellow;

				foreach (var selected in SelectedNodes)
					RoadGizmos.DrawNode(transform.PointToWorld(selected.Position), true);

				return;
			}

			if (SelectedNode is not { } node)
				return;

			Vector3 nodeWorld = transform.PointToWorld(node.Position);

			Gizmo.Draw.Color = Color.Yellow;
			RoadGizmos.DrawNode(nodeWorld, true);

			// Tangent handles: one per road leaving the node.
			foreach (var attached in _Network.GetSegmentsAt(node.Id))
			{
				Vector3 handleWorld = transform.PointToWorld(node.Position + RoadNetwork.GetHandle(attached, node.Id));
				bool active = attached.Id == _HoverHandleSegment || (m_Drag == DragKind.Handle && attached.Id == m_DragSegmentId);

				// Handles the network works out entirely are drawn faint; ones whose direction was placed by hand, solid.
				bool computed = node.TangentMode == AutoTangentMode.Full;

				Gizmo.Draw.Color = computed ? Color.White.WithAlpha(0.5f) : Color.White;
				Gizmo.Draw.LineThickness = 1.0f;
				Gizmo.Draw.Line(nodeWorld, handleWorld);

				Gizmo.Draw.Color = active ? Color.Cyan : (computed ? Color.White.WithAlpha(0.6f) : Color.White);
				Gizmo.Draw.SolidSphere(handleWorld, RoadPicker.NodeRadius(handleWorld) * 0.45f);
			}
		}
	}
}



/// <summary>
/// A node's Auto Tangents setting, set through the network rather than on the node, so the handles follow at once and
/// the network knows which mode the node is coming from (a node coming off Full gets the straight handles of a sharp
/// corner). Wrapped in an object of its own so the sidebar can edit it for several nodes at once.
/// </summary>
public sealed class RoadNodeFlags
{
	private readonly RoadNetwork m_Network;
	private readonly int m_NodeId;



	public RoadNodeFlags(RoadNetwork _Network, int _NodeId)
	{
		m_Network = _Network;
		m_NodeId = _NodeId;
	}



	/// <summary>
	/// How the node's tangent handles are kept up.
	/// </summary>
	[Property, Title("Auto Tangents")]
	public AutoTangentMode AutoTangents
	{
		get => m_Network.IsValid() ? m_Network.FindNode(m_NodeId)?.TangentMode ?? AutoTangentMode.Full : AutoTangentMode.Full;
		set
		{
			if (m_Network.IsValid() && m_Network.FindNode(m_NodeId) is not null)
				m_Network.SetTangentMode(m_NodeId, value);
		}
	}
}



/// <summary>
/// A road's two settings that are not fields of the road: One Way is worked out from its lanes and Sharp Corners from
/// its two end nodes. Wrapped in an object of their own so the sidebar can edit them like any other property, and for
/// several roads at once through the same multi-object editing the inspector uses (which also shows where the roads
/// differ).
/// </summary>
public sealed class RoadSegmentFlags
{
	private readonly RoadNetwork m_Network;
	private readonly int m_SegmentId;



	public RoadSegmentFlags(RoadNetwork _Network, int _SegmentId)
	{
		m_Network = _Network;
		m_SegmentId = _SegmentId;
	}



	private RoadSegment Segment => m_Network.IsValid() ? m_Network.FindSegment(m_SegmentId) : null;



	/// <summary>
	/// The road runs one way: all its lanes go the same direction. Turning it on sends every lane from the road's
	/// start to its end; turning it off splits the lanes between the two directions again. Only the painted lines
	/// change. The Lanes tool sets the split lane by lane.
	/// </summary>
	[Property, Title("One Way")]
	public bool OneWay
	{
		get => Segment?.IsOneWay ?? false;
		set
		{
			if (Segment is not { } segment || segment.IsOneWay == value)
				return;

			int lanes = segment.LaneCount;
			m_Network.SetLanes(segment.Id, lanes, value ? lanes : RoadSegment.DefaultForwardLanes(lanes));
		}
	}



	/// <summary>
	/// The road leaves both its ends in a straight line (a grid street corner) instead of flowing smoothly into the
	/// roads beside it. It is the Auto Tangents mode of the road's two end nodes: on takes them off Full and
	/// straightens their handles, off hands them back to the network. Every other road at those nodes follows.
	/// </summary>
	[Property, Title("Sharp Corners")]
	public bool SharpCorners
	{
		get => Segment is { } segment
			&& m_Network.FindNode(segment.StartNodeId)?.TangentMode != AutoTangentMode.Full
			&& m_Network.FindNode(segment.EndNodeId)?.TangentMode != AutoTangentMode.Full;
		set
		{
			if (Segment is not { } segment)
				return;

			var mode = value ? AutoTangentMode.Disabled : AutoTangentMode.Full;

			m_Network.SetTangentMode(segment.StartNodeId, mode);
			m_Network.SetTangentMode(segment.EndNodeId, mode);
		}
	}
}



file class RoadSelectToolWidget : ToolSidebarWidget
{
	private readonly RoadSelectTool m_Tool;
	private int m_ShownVersion = -1;
	private IDisposable m_UndoScope;



	public RoadSelectToolWidget(RoadSelectTool _Tool)
	{
		m_Tool = _Tool;

		Rebuild();
	}



	[EditorEvent.Frame]
	public void Frame()
	{
		if (m_Tool.SelectionVersion != m_ShownVersion)
			Rebuild();
	}



	private void Rebuild()
	{
		m_ShownVersion = m_Tool.SelectionVersion;

		Layout.Clear(true);
		AddTitle("Edit Road", "edit");

		var network = m_Tool.Network;

		if (network.IsValid() && m_Tool.SelectedNodes.ToList() is { Count: > 0 } nodes)
			BuildNodeSection(network, nodes);
		else if (network.IsValid() && m_Tool.SelectedSegments.ToList() is { Count: > 0 } segments)
			BuildSegmentSection(network, segments);
		else
			AddGroup("Selection").Add(new WrappedLabel("Click a node or a road to select it. Shift+click adds more."));

		AddShortcuts(
			("Select", "LMB or SHIFT + LMB"),
			("Drag Node / Tangent", "LMB Drag"),
			("Straight Tangent (no mirror)", "ALT + Drag"),
			("Insert Node On Road", "CTRL + LMB"),
			("Delete Selection", "DEL"),
			("Deselect", "ESC")
		);

		Layout.AddStretchCell();
	}



	private void BuildNodeSection(RoadNetwork _Network, List<RoadNode> _Nodes)
	{
		bool multiple = _Nodes.Count > 1;
		var group = AddGroup(multiple ? $"{_Nodes.Count} Nodes" : $"Node #{_Nodes[0].Id}");

		// One object over every selected node, like the roads: a write goes to all of them and a value that differs is
		// shown as such.
		var serialized = Combine(_Nodes.Select(node => node.GetSerialized()));

		serialized.OnPropertyStartEdit += _ => BeginEdit(multiple ? "Edit Nodes" : "Edit Node");
		serialized.OnPropertyFinishEdit += _ => EndEdit();
		serialized.OnPropertyChanged += property =>
		{
			foreach (var node in _Nodes)
				m_Tool.OnNodeEdited(node, property?.Name);
		};

		// The position can only be set for one node. It is read off the tool rather than the node, so it can be shown
		// in the node's own frame as well as the network's; its setter moves the node either way.
		if (!multiple)
		{
			var tool = m_Tool.GetSerialized();

			tool.OnPropertyStartEdit += _ => BeginEdit("Move Node");
			tool.OnPropertyFinishEdit += _ => EndEdit();

			SidebarRows.Add(group, tool.GetProperty(nameof(RoadSelectTool.NodeSpace)));
			SidebarRows.AddVector(group, tool.GetProperty(nameof(RoadSelectTool.NodePosition)));
		}

		var flags = Combine(_Nodes.Select(node => new RoadNodeFlags(_Network, node.Id).GetSerialized()));

		flags.OnPropertyStartEdit += _ => BeginEdit(multiple ? "Edit Nodes" : "Edit Node");
		flags.OnPropertyFinishEdit += _ => EndEdit();

		SidebarRows.Add(group, flags.GetProperty(nameof(RoadNodeFlags.AutoTangents)));
		SidebarRows.Add(group, serialized.GetProperty(nameof(RoadNode.CornerRadius)));

		if (!multiple)
			group.Add(new Label($"Roads at this node: {_Network.GetDegree(_Nodes[0].Id)}"));

		var row = group.AddRow();
		row.Spacing = 4;
		row.Add(new Button(multiple ? "Delete Nodes" : "Delete Node", "delete") { Clicked = m_Tool.DeleteSelection });
	}



	private void BuildSegmentSection(RoadNetwork _Network, List<RoadSegment> _Segments)
	{
		bool multiple = _Segments.Count > 1;
		var group = AddGroup(multiple ? $"{_Segments.Count} Roads" : $"Road #{_Segments[0].Id}");

		// One object over every selected road: a write goes to all of them, and a property whose value differs between
		// them is shown greyed out (or as "Multiple Values"), the way the inspector shows several objects.
		var serialized = Combine(_Segments.Select(segment => segment.GetSerialized()));

		serialized.OnPropertyStartEdit += _ => BeginEdit(multiple ? "Edit Roads" : "Edit Road");
		serialized.OnPropertyFinishEdit += _ => EndEdit();
		serialized.OnPropertyChanged += _ => m_Tool.OnSegmentEdited();

		SidebarRows.AddWide(group, serialized.GetProperty(nameof(RoadSegment.Profile)));
		SidebarRows.AddWide(group, serialized.GetProperty(nameof(RoadSegment.Bridge)));
		SidebarRows.AddWide(group, serialized.GetProperty(nameof(RoadSegment.Props)));
		SidebarRows.Add(group, serialized.GetProperty(nameof(RoadSegment.Width)));

		// One Way and Sharp Corners are not fields of the road, so they are edited through a small object per road.
		var flags = Combine(_Segments.Select(segment => new RoadSegmentFlags(_Network, segment.Id).GetSerialized()));

		flags.OnPropertyStartEdit += _ => BeginEdit(multiple ? "Edit Roads" : "Edit Road");
		flags.OnPropertyFinishEdit += _ => EndEdit();
		flags.OnPropertyChanged += _ => m_Tool.OnSegmentEdited();

		var oneWay = SidebarRows.Add(group, flags.GetProperty(nameof(RoadSegmentFlags.OneWay)));

		// A single lane has only one direction to go, so it is always one way: shown ticked, and not to be changed.
		if (oneWay is not null && _Segments.All(segment => segment.LaneCount <= 1))
		{
			const string reason = "Roads with a single lane are always one way.";

			oneWay.Enabled = false;
			oneWay.ToolTip = reason;

			foreach (var child in oneWay.Children)
				child.ToolTip = reason;
		}

		SidebarRows.Add(group, flags.GetProperty(nameof(RoadSegmentFlags.SharpCorners)));
		SidebarRows.Add(group, serialized.GetProperty(nameof(RoadSegment.ConformTerrain)));

		float length = _Segments.Sum(segment => _Network.GetCurveTable(segment)?.Length ?? 0.0f);
		group.Add(new Label(multiple ? $"Total length: {length:0} units" : $"Length: {length:0} units"));

		var row = group.AddRow();
		row.Spacing = 4;
		row.Add(new Button(multiple ? "Delete Roads" : "Delete Road", "delete") { Clicked = m_Tool.DeleteSelection });

		// How the edges run: extra width per side at each end (a lane appearing over a taper), and the ramp
		// treatment for a road leaving another from its side.
		var shape = AddGroup("Edges");

		SidebarRows.Add(shape, serialized.GetProperty(nameof(RoadSegment.LeftExtraStart)));
		SidebarRows.Add(shape, serialized.GetProperty(nameof(RoadSegment.LeftExtraEnd)));
		SidebarRows.Add(shape, serialized.GetProperty(nameof(RoadSegment.RightExtraStart)));
		SidebarRows.Add(shape, serialized.GetProperty(nameof(RoadSegment.RightExtraEnd)));
		SidebarRows.Add(shape, serialized.GetProperty(nameof(RoadSegment.Ramp)));
	}



	/// <summary>
	/// One object when there is one, otherwise a multi-object over all of them.
	/// </summary>
	private static SerializedObject Combine(IEnumerable<SerializedObject> _Objects)
	{
		var list = _Objects.ToList();

		if (list.Count == 1)
			return list[0];

		var multi = new MultiSerializedObject();

		foreach (var item in list)
			multi.Add(item);

		multi.Rebuild();

		return multi;
	}



	private void BeginEdit(string _Name)
	{
		var network = m_Tool.Network;

		if (!network.IsValid())
			return;

		m_UndoScope ??= SceneEditorSession.Active.UndoScope(_Name).WithComponentChanges(network).Push();
	}



	private void EndEdit()
	{
		m_UndoScope?.Dispose();
		m_UndoScope = null;
	}



	[Shortcut("roadtool2.delete-selection", "DEL", typeof(SceneViewWidget))]
	private void DeleteSelection()
	{
		m_Tool.DeleteSelection();
	}



	[Shortcut("roadtool2.deselect", "ESC", typeof(SceneViewWidget))]
	private void Deselect()
	{
		m_Tool.ClearSelection();
	}
}
