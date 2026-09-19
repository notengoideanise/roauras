namespace RefugeCooldownOverlay.Core;

/// <summary>
/// Pure overlay grid layout — unit-testable without a display.
/// </summary>
public static class OverlayLayout
{
    public sealed record Placement(int Index, int X, int Y);

    /// <summary>
    /// Row-major grid positions. Columns is clamped to at least 1; the last row may
    /// be partial. When <paramref name="rows"/> is 0 the grid auto-grows; when
    /// positive, the grid is capped at columns × rows tiles and overflow tiles are
    /// dropped (never reflowed), keeping the layout stable. Ordering is stable.
    /// </summary>
    public static IReadOnlyList<Placement> Calculate(
        int count, int columns, int iconSize, int spacing,
        int originX = 0, int originY = 0, int rows = 0)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (columns < 1) columns = 1;
        if (iconSize < 1) iconSize = 1;
        if (spacing < 0) spacing = 0;
        if (rows < 0) rows = 0;

        var capacity = rows > 0 ? columns * rows : count;
        var step = iconSize + spacing;
        var placements = new List<Placement>(Math.Min(count, capacity));
        for (var index = 0; index < count && placements.Count < capacity; index++)
        {
            var column = index % columns;
            var row = index / columns;
            placements.Add(new Placement(
                index,
                originX + column * step,
                originY + row * step));
        }

        return placements;
    }
}

/// <summary>
/// PRM-relative anchor math. The overlay anchor is always an offset from the
/// attached PRM window's top-left corner — never a screen coordinate — so moving
/// PRM moves the overlay with it.
/// </summary>
public static class AnchorMath
{
    /// <summary>Anchor offset for an overlay point, relative to a window origin.</summary>
    public static (int AnchorX, int AnchorY) AnchorFromScreen(
        int screenX, int screenY, int windowX, int windowY) =>
        (screenX - windowX, screenY - windowY);

    /// <summary>Screen position of the overlay for an anchor and window origin.</summary>
    public static (int ScreenX, int ScreenY) ScreenFromAnchor(
        int anchorX, int anchorY, int windowX, int windowY) =>
        (windowX + anchorX, windowY + anchorY);
}

/// <summary>
/// Deterministic monogram for tiles without icon art: never fabricated artwork,
/// never the old name strip under the cell.
/// </summary>
public static class SkillMonogram
{
    public static string For(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "?";
        }

        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2)
        {
            return $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}";
        }

        var single = words[0];
        return single.Length >= 2
            ? char.ToUpperInvariant(single[0]) + char.ToUpperInvariant(single[1]).ToString()
            : char.ToUpperInvariant(single[0]).ToString();
    }
}
