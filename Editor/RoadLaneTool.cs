using System;
using Editor;
using Sandbox;

namespace RedSnail.RoadTool2.Editor;

/// <summary>
/// Lanes: hover a road to see its lanes and their directions, click to add a lane (wrapping back to one after the
/// most), right-click to cycle the direction between two-way, one-way and one-way the other way. The painted
/// lines follow from the lanes on their own.
/// </summary>
[Title("Lanes")]
[Icon("view_column")]
[Group("1")]
[Order(2)]
public class RoadLaneTool : EditorTool
{
	/// <summary>
	/// A right button release this close to where it was pressed is a click; anything further is the camera orbiting.
	/// </summary>
	private const float ClickDragTolerance = 4.0f;

	/// <summary>
	/// How far apart the direction arrows sit along a lane.
	/// </summary>
	private const float ArrowSpacing = 400.0f;

	/// <summary>
	/// The last road clicked, whose lanes the sidebar edits.
	/// </summary>
	public int SelectedSegmentId { get; private set; } = -1;

	/// <summary>
	/// Bumped whenever the selection or its lanes change, so the sidebar knows to rebuild itself.
	/// </summary>
	public int SelectionVersion { get; private set; }

	public RoadNetwork Network => Root?.Network;

	public RoadSegment SelectedSegment => Network.IsValid() ? Network.FindSegment(SelectedSegmentId) : null;

	private RoadNetworkTool Root => Manager?.CurrentTool as RoadNetworkTool;

	private Vector2 m_RightPressPosition;



	public override Widget CreateToolSidebar()
	{
		return new RoadLaneToolWidget(this);
	}



	public override void OnUpdate()
	{
		var network = Network;

		if (!network.IsValid())
		{
			Select(null);
			return;
		}

		if (SelectedSegmentId >= 0 && network.FindSegment(SelectedSegmentId) is null)
			Select(null);

		bool canPick = Gizmo.HasMouseFocus && !Gizmo.HasHovered;
		RoadSegment hovered = canPick ? RoadPicker.PickSegment(network, Gizmo.CurrentRay)?.Segment : null;

		if (SelectedSegment is { } selected && selected != hovered)
			DrawLanes(network, selected, false);

		if (hovered is not null)
			DrawLanes(network, hovered, true);

		if (!canPick)
			return;

		if (Gizmo.WasLeftMousePressed && hovered is not null)
		{
			Select(hovered);
			CycleLaneCount(network, hovered);
		}

		if (Gizmo.WasRightMousePressed)
			m_RightPressPosition = Gizmo.CursorPosition;

		if (Gizmo.WasRightMouseReleased && hovered is not null && Vector2.Distance(Gizmo.CursorPosition, m_RightPressPosition) < ClickDragTolerance)
		{
			Select(hovered);
			CycleDirection(network, hovered);
		}
	}



	public void Select(RoadSegment _Segment)
	{
		int id = _Segment?.Id ?? -1;

		if (id == SelectedSegmentId)
			return;

		SelectedSegmentId = id;
		SelectionVersion++;
	}



	/// <summary>
	/// The sidebar edited the selected road's lanes directly; push it through the network so the lines follow.
	/// </summary>
	public void OnLanesEdited()
	{
		var network = Network;

		if (!network.IsValid() || SelectedSegment is not { } segment)
			return;

		network.SetLanes(segment.Id, segment.LaneCount, segment.ForwardLanes);
		SelectionVersion++;
	}



	/// <summary>
	/// One more lane, wrapping round to one after the most, keeping an even two-way split (the odd lane goes
	/// forward). A one-way road stays one-way.
	/// </summary>
	private void CycleLaneCount(RoadNetwork _Network, RoadSegment _Segment)
	{
		int count = _Segment.LaneCount >= RoadSegment.MaxLanes ? RoadSegment.MinLanes : _Segment.LaneCount + 1;
		int forward = _Segment.ForwardLanes <= 0 ? 0 : _Segment.ForwardLanes >= _Segment.LaneCount ? count : RoadSegment.DefaultForwardLanes(count);

		Apply(_Network, _Segment, count, forward, "Change Lanes");
	}



	/// <summary>
	/// Two-way, then one-way start to end, then one-way end to start, then two-way again. A single lane just
	/// flips its direction.
	/// </summary>
	private void CycleDirection(RoadNetwork _Network, RoadSegment _Segment)
	{
		int count = _Segment.LaneCount;
		int forward;

		if (count <= 1)
			forward = _Segment.ForwardLanes > 0 ? 0 : 1;
		else if (_Segment.ForwardLanes >= count)
			forward = 0;
		else if (_Segment.ForwardLanes <= 0)
			forward = RoadSegment.DefaultForwardLanes(count);
		else
			forward = count;

		Apply(_Network, _Segment, count, forward, "Change Lane Direction");
	}



	private void Apply(RoadNetwork _Network, RoadSegment _Segment, int _LaneCount, int _ForwardLanes, string _UndoName)
	{
		using (SceneEditorSession.Active.UndoScope(_UndoName).WithComponentChanges(_Network).Push())
			_Network.SetLanes(_Segment.Id, _LaneCount, _ForwardLanes);

		SelectionVersion++;
	}



	/// <summary>
	/// The road's outline, its lane boundaries, and an arrow per lane every so often showing which way it runs.
	/// </summary>
	private static void DrawLanes(RoadNetwork _Network, RoadSegment _Segment, bool _Hovered)
	{
		var table = _Network.GetCurveTable(_Segment);

		if (table is null || !_Network.TryGetCurve(_Segment, out var curve))
			return;

		var transform = _Network.WorldTransform;
		var width = _Network.WidthOf(_Segment);
		float lift = _Network.SurfaceOffset + 2.0f;
		int lanes = _Segment.LaneCount;
		float laneWidth = _Segment.Width / lanes;
		float halfWidth = _Segment.Width * 0.5f;

		// The same rule the paint follows: a road pulled in anywhere has its lanes across whatever surface is
		// there, one that only grows keeps them on its base width.
		bool reshaped = _Segment.LeftExtraStart < 0.0f || _Segment.LeftExtraEnd < 0.0f || _Segment.RightExtraStart < 0.0f || _Segment.RightExtraEnd < 0.0f;

		float OffsetAt(float _Fraction, float _Distance)
		{
			return reshaped ? -width.LeftAt(_Distance) + _Fraction * width.WidthAt(_Distance) : -halfWidth + _Fraction * _Segment.Width;
		}

		using (Gizmo.Scope($"roadtool2_lanes_{_Segment.Id}"))
		{
			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.LineThickness = _Hovered ? 2.0f : 1.0f;
			Gizmo.Draw.Color = (_Hovered ? Color.Orange : Color.White).WithAlpha(_Hovered ? 0.9f : 0.5f);
			RoadGizmos.DrawRoadOutline(curve, width, transform);

			// Lane boundaries, coloured by what they separate: the centre line stands out.
			for (int boundary = 1; boundary < lanes; boundary++)
			{
				bool centre = _Segment.IsLaneForward(boundary - 1) != _Segment.IsLaneForward(boundary);
				float fraction = (float)boundary / lanes;

				Gizmo.Draw.Color = (centre ? Color.Yellow : Color.White).WithAlpha(_Hovered ? 0.9f : 0.4f);
				DrawOffsetLine(table, transform, distance => OffsetAt(fraction, distance), lift);
			}

			// Arrows down the middle of each lane.
			Gizmo.Draw.LineThickness = _Hovered ? 3.0f : 2.0f;
			Gizmo.Draw.Color = (_Hovered ? Color.Cyan : Color.White).WithAlpha(_Hovered ? 1.0f : 0.5f);

			int arrows = Math.Max(1, (int)MathF.Floor(table.Length / ArrowSpacing));
			float arrowLength = MathF.Min(laneWidth * 0.8f, 120.0f);

			for (int lane = 0; lane < lanes; lane++)
			{
				float fraction = (lane + 0.5f) / lanes;
				float sign = _Segment.IsLaneForward(lane) ? 1.0f : -1.0f;

				for (int i = 0; i < arrows; i++)
				{
					float distance = table.Length * (i + 0.5f) / arrows;
					DrawArrow(table, transform, distance, OffsetAt(fraction, distance), sign, arrowLength, lift);
				}
			}
		}
	}



	private static void DrawOffsetLine(RoadCurveTable _Table, in Transform _Transform, Func<float, float> _OffsetAt, float _Lift)
	{
		Vector3 previous = default;

		for (int i = 0; i < _Table.SampleCount; i++)
		{
			float distance = _Table.SampleDistance(i);
			Vector3 point = _Transform.PointToWorld(_Table.PositionAt(distance) + RoadCurve.RightOf(_Table.TangentAt(distance)) * _OffsetAt(distance) + Vector3.Up * _Lift);

			if (i > 0)
				Gizmo.Draw.Line(previous, point);

			previous = point;
		}
	}



	private static void DrawArrow(RoadCurveTable _Table, in Transform _Transform, float _Distance, float _Offset, float _Sign, float _Length, float _Lift)
	{
		Vector3 tangent = _Table.TangentAt(_Distance) * _Sign;
		Vector3 right = RoadCurve.RightOf(_Table.TangentAt(_Distance));
		Vector3 centre = _Table.PositionAt(_Distance) + right * _Offset + Vector3.Up * _Lift;
		Vector3 tail = _Transform.PointToWorld(centre - tangent * _Length * 0.5f);
		Vector3 tip = _Transform.PointToWorld(centre + tangent * _Length * 0.5f);
		Vector3 headBase = _Transform.PointToWorld(centre + tangent * _Length * 0.15f);
		Vector3 side = _Transform.NormalToWorld(right) * _Length * 0.3f;

		Gizmo.Draw.Line(tail, tip);
		Gizmo.Draw.Line(tip, headBase + side);
		Gizmo.Draw.Line(tip, headBase - side);
	}
}



file class RoadLaneToolWidget : ToolSidebarWidget
{
	private readonly RoadLaneTool m_Tool;
	private int m_ShownVersion = -1;
	private IDisposable m_UndoScope;



	public RoadLaneToolWidget(RoadLaneTool _Tool)
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
		AddTitle("Lanes", "view_column");

		var network = m_Tool.Network;

		if (network.IsValid() && m_Tool.SelectedSegment is { } segment)
		{
			var group = AddGroup($"Road #{segment.Id}");
			var serialized = segment.GetSerialized();

			serialized.OnPropertyStartEdit += _ => BeginEdit();
			serialized.OnPropertyFinishEdit += _ => EndEdit();
			serialized.OnPropertyChanged += _ => m_Tool.OnLanesEdited();

			SidebarRows.Add(group, serialized.GetProperty(nameof(RoadSegment.LaneCount)));
			SidebarRows.Add(group, serialized.GetProperty(nameof(RoadSegment.ForwardLanes)));

			string direction = segment.ForwardLanes >= segment.LaneCount ? "one-way, start to end"
				: segment.ForwardLanes <= 0 ? "one-way, end to start"
				: $"two-way, {segment.ForwardLanes} forward / {segment.BackwardLanes} back";

			group.Add(new WrappedLabel($"{segment.LaneCount} lane{(segment.LaneCount == 1 ? "" : "s")}, {direction}"));

			var crossings = AddGroup("Crossings");
			var profile = network.ProfileOf(segment);

			if (profile.Crossings)
			{
				SidebarRows.Add(crossings, serialized.GetProperty(nameof(RoadSegment.CrossingAtStart)));
				SidebarRows.Add(crossings, serialized.GetProperty(nameof(RoadSegment.CrossingAtEnd)));
				crossings.Add(new WrappedLabel("Drawn where the end meets a junction."));
			}
			else
			{
				crossings.Add(new WrappedLabel($"The road type \"{profile.ResourceName}\" has no crossings; turn them on in the profile."));
			}
		}
		else
		{
			AddGroup("Lanes").Add(new WrappedLabel("Hover a road to see its lanes; click it to add one, right-click to change direction."));
		}

		AddShortcuts(
			("Add Lane (1 → 6 → 1)", "LMB on road"),
			("Cycle Direction", "RMB on road")
		);

		Layout.AddStretchCell();
	}



	private void BeginEdit()
	{
		var network = m_Tool.Network;

		if (!network.IsValid())
			return;

		m_UndoScope ??= SceneEditorSession.Active.UndoScope("Edit Lanes").WithComponentChanges(network).Push();
	}



	private void EndEdit()
	{
		m_UndoScope?.Dispose();
		m_UndoScope = null;
	}
}
