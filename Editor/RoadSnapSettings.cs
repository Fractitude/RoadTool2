namespace RedSnail.RoadTool2.Editor;

/// <summary>
/// The Snapping settings the Draw and Edit tools share: one set of values, so a change made in one tool is there in
/// the other. The tools' own properties read and write these. Not readonly, so a hot reload keeps them.
/// </summary>
internal static class RoadSnapSettings
{
	public static bool AngleSnap = false;
	public static float AngleStep = 10.0f;
	public static bool GridSnap = false;
	public static float GridSize = 64.0f;
	public static bool NodeSnap = false;
}
