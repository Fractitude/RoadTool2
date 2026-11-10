using System;
using System.Collections.Generic;
using Editor;
using Sandbox;

namespace RedSnail.RoadTool2.Editor;

/// <summary>
/// Text that wraps to the sidebar's width and is as tall as it needs to be. A wrapping <see cref="Label"/> asks for one
/// line of height whatever its text, so in a column it is cut off after the first line or two; this one breaks the
/// text into lines itself each time it is drawn and sets its own height to fit them.
/// </summary>
internal sealed class WrappedLabel : Widget
{
	// A widget with no height is never painted, so it could never measure itself: it starts one line tall.
	private const float LineHeight = 18.0f;

	private string m_Text;
	private float m_Height;



	public WrappedLabel(string _Text)
	{
		m_Text = _Text ?? "";
		SetHeight(m_Text.Length > 0 ? LineHeight : 0.0f);
	}



	public string Text
	{
		get => m_Text;
		set
		{
			value ??= "";

			if (m_Text == value)
				return;

			m_Text = value;
			SetHeight(m_Text.Length > 0 ? LineHeight : 0.0f);
			Update();
		}
	}



	private void SetHeight(float _Height)
	{
		m_Height = _Height;
		FixedHeight = _Height;
	}



	protected override void OnPaint()
	{
		if (m_Text.Length == 0 || Width <= 1.0f)
			return;

		Paint.SetDefaultFont();
		Paint.SetPen(Theme.TextControl);

		var lines = Wrap(Width);
		float y = 0.0f;

		foreach (string line in lines)
		{
			Paint.DrawText(new Rect(0.0f, y, Width, LineHeight), line, TextFlag.LeftCenter);
			y += LineHeight;
		}

		// Growing the widget lays the sidebar out again, and the next paint draws it at the new size.
		float height = lines.Count * LineHeight + 2.0f;

		if (MathF.Abs(m_Height - height) > 0.5f)
		{
			SetHeight(height);
			Update();
		}
	}



	/// <summary>
	/// The text broken into lines no wider than the given width, at spaces (a word longer than a line gets a line of
	/// its own).
	/// </summary>
	private List<string> Wrap(float _Width)
	{
		var lines = new List<string>();
		string current = "";

		foreach (string word in m_Text.Split(' '))
		{
			string candidate = current.Length == 0 ? word : current + " " + word;

			if (current.Length > 0 && Paint.MeasureText(candidate).x > _Width)
			{
				lines.Add(current);
				current = word;
			}
			else
			{
				current = candidate;
			}
		}

		lines.Add(current);

		return lines;
	}
}



/// <summary>
/// Property rows sized for the tool sidebar. The stock control sheet row keeps a 140 wide label column next to a
/// control that asks for 200, which the 240 wide sidebar cannot hold: the control runs off the edge and shows a
/// few clipped characters. These rows give the label a short fixed width and hand the control the rest, or put
/// the label above a full-width control for the ones that need the room (a resource picker), or one full-width
/// field per axis for a vector. A checkbox stays a checkbox.
/// </summary>
internal static class SidebarRows
{
	private const float LabelWidth = 80.0f;
	private const float Spacing = 4.0f;



	/// <summary>
	/// Label beside the control.
	/// </summary>
	public static Widget Add(Layout _Group, SerializedProperty _Property)
	{
		return AddRow(_Group, _Property, false);
	}



	/// <summary>
	/// Label above the control, which then spans the sidebar.
	/// </summary>
	public static Widget AddWide(Layout _Group, SerializedProperty _Property)
	{
		return AddRow(_Group, _Property, true);
	}



	/// <summary>
	/// A vector as its label followed by one full-width field per axis, each badged with its axis like the stock
	/// vector control's fields. Three fields side by side do not fit a sidebar.
	/// </summary>
	public static Widget AddVector(Layout _Group, SerializedProperty _Property)
	{
		if (!_Property.TryGetAsObject(out SerializedObject components) || components is null)
			return AddWide(_Group, _Property);

		string tooltip = _Property.Description ?? _Property.DisplayName;

		var block = new Widget
		{
			Layout = Layout.Column(),
			ToolTip = tooltip
		};

		block.Layout.Spacing = 2.0f;
		block.Layout.Add(new Label(_Property.DisplayName) { ToolTip = tooltip });

		AddAxis(block.Layout, components.GetProperty("x"), Theme.Red, "X");
		AddAxis(block.Layout, components.GetProperty("y"), Theme.Green, "Y");
		AddAxis(block.Layout, components.GetProperty("z"), Theme.Blue, "Z");
		AddAxis(block.Layout, components.GetProperty("w"), Theme.Yellow, "W");

		_Group.Add(block);

		return block;
	}



	private static void AddAxis(Layout _Layout, SerializedProperty _Axis, Color _Color, string _Badge)
	{
		if (_Axis is null)
			return;

		var control = new FloatControlWidget(_Axis)
		{
			HighlightColor = _Color,
			Label = _Badge,
			MinimumWidth = 0.0f,
			HorizontalSizeMode = SizeMode.Flexible
		};

		_Layout.Add(control);
	}



	private static Widget AddRow(Layout _Group, SerializedProperty _Property, bool _Wide)
	{
		var control = ControlWidget.Create(_Property);

		if (control is null)
			return null;

		string tooltip = _Property.Description ?? _Property.DisplayName;
		bool checkbox = _Property.PropertyType == typeof(bool);

		// The control's default minimum of 200 is what pushes it off the sidebar.
		control.MinimumWidth = 0.0f;
		control.HorizontalSizeMode = SizeMode.Flexible;
		control.ToolTip = tooltip;

		// A checkbox is square; stretched it turns into a bar.
		if (checkbox)
			control.FixedWidth = Theme.RowHeight;

		var label = new Label(_Property.DisplayName)
		{
			ToolTip = tooltip,
			Alignment = TextFlag.LeftCenter
		};

		var row = new Widget
		{
			Layout = _Wide && !checkbox ? Layout.Column() : Layout.Row(),
			ToolTip = tooltip
		};

		row.Layout.Spacing = Spacing;

		if (_Wide && !checkbox)
		{
			row.Layout.Add(label);
			row.Layout.Add(control);
		}
		else
		{
			label.FixedWidth = LabelWidth;
			row.Layout.Add(label);
			row.Layout.Add(control, checkbox ? 0 : 1);

			if (checkbox)
				row.Layout.AddStretchCell();
		}

		_Group.Add(row);

		return row;
	}
}
