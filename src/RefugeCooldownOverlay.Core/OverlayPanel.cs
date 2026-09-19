namespace RefugeCooldownOverlay.Core;

/// <summary>Preset tile arrangements for the in-game overlay grid.</summary>
public enum OverlayLayoutPreset
{
    /// <summary>Row-major rectangle using Columns/Rows (the classic grid).</summary>
    Grid,

    /// <summary>All skills in one horizontal line.</summary>
    Horizontal,

    /// <summary>Two columns — skills laid out in pairs.</summary>
    Pairs,

    /// <summary>Five-cell block shaped like a 3: top 2, center-right 1, bottom 2. Repeats side by side.</summary>
    Number3,

    /// <summary>User-supplied occupied-cell pattern, repeated side by side.</summary>
    Custom,
}

/// <summary>One occupied grid cell (column, row). Only occupied cells exist — the panel never reserves empty grid capacity.</summary>
public readonly record struct OverlayCell(int X, int Y);

/// <summary>
/// Occupied-cell layout and panel measurement. Pure seam so tests can pin the
/// contract without WinForms: panels hug occupied tiles only — with 4 skills and
/// 3 columns the panel spans 3+1 cells, never the reserved 3×2 grid.
/// </summary>
public static class OverlayPanel
{
    private static readonly OverlayCell[] Number3Pattern =
    {
        new(0, 0), new(1, 0), new(1, 1), new(0, 2), new(1, 2),
    };

    /// <summary>
    /// Occupied cells for <paramref name="count"/> skills under the preset.
    /// Grid honors Columns and the Rows capacity cap; the shape presets ignore
    /// Columns/Rows and repeat their pattern horizontally with a one-cell gap.
    /// </summary>
    public static IReadOnlyList<OverlayCell> CellsFor(
        int count, int columns, int rows,
        OverlayLayoutPreset preset, IReadOnlyList<OverlayCell>? custom)
    {
        var cells = new List<OverlayCell>(Math.Max(0, count));
        if (count <= 0)
        {
            return cells;
        }

        switch (preset)
        {
            case OverlayLayoutPreset.Horizontal:
                for (var i = 0; i < count; i++)
                {
                    cells.Add(new OverlayCell(i, 0));
                }

                break;
            case OverlayLayoutPreset.Pairs:
                for (var i = 0; i < count; i++)
                {
                    cells.Add(new OverlayCell(i % 2, i / 2));
                }

                break;
            case OverlayLayoutPreset.Number3:
                AppendRepeated(cells, count, Number3Pattern);
                break;
            case OverlayLayoutPreset.Custom when custom is { Count: > 0 }:
                cells.AddRange(ResolveCustom(count, Normalize(custom)));
                break;
            default: // Grid, and Custom with no valid pattern
                if (columns < 1)
                {
                    columns = 1;
                }

                var capacity = rows > 0 ? columns * rows : count;
                for (var i = 0; i < count && cells.Count < capacity; i++)
                {
                    cells.Add(new OverlayCell(i % columns, i / columns));
                }

                break;
        }

        return cells;
    }

    /// <summary>Panel size that hugs the occupied cells plus padding; empty state shrinks to a minimal footprint.</summary>
    public static (int Width, int Height) Measure(
        IReadOnlyList<OverlayCell> cells, int iconSize, int spacing, int padding)
    {
        if (padding < 0)
        {
            padding = 0;
        }

        if (cells.Count == 0)
        {
            var minimal = Math.Max(8, padding * 2);
            return (minimal, minimal);
        }

        var maxX = cells.Max(c => c.X);
        var maxY = cells.Max(c => c.Y);
        return ((maxX + 1) * iconSize + maxX * spacing + padding * 2,
            (maxY + 1) * iconSize + maxY * spacing + padding * 2);
    }

    /// <summary>Grid-preset convenience measure: row-major cells, then hug them.</summary>
    public static (int Width, int Height) Measure(
        int count, int columns, int iconSize, int spacing, int rows, int padding) =>
        Measure(CellsFor(count, columns, rows, OverlayLayoutPreset.Grid, null), iconSize, spacing, padding);

    public static IReadOnlyList<OverlayCell> Normalize(IReadOnlyList<OverlayCell> cells)
    {
        if (cells.Count == 0)
        {
            return cells;
        }

        var minY = cells.Min(c => c.Y);
        return cells.Select(c => new OverlayCell(c.X, c.Y - minY)).ToList();
    }

    /// <summary>Resolve bounded, duplicate-free custom positions; invalid cells get grid-like fallback slots.</summary>
    public static IReadOnlyList<OverlayCell> ResolveCustom(
        int count, IReadOnlyList<OverlayCell> cells, int bound = 12)
    {
        var result = new List<OverlayCell>();
        var used = new HashSet<OverlayCell>();
        for (var index = 0; index < count; index++)
        {
            var candidate = index < cells.Count ? cells[index] : new OverlayCell(index % bound, index / bound);
            if (candidate.X < 0 || candidate.Y < 0 || candidate.X >= bound || candidate.Y >= bound || !used.Add(candidate))
            {
                var row = 0;
                var column = 0;
                do
                {
                    candidate = new OverlayCell(column, row);
                    column++;
                    if (column >= bound)
                    {
                        column = 0;
                        row++;
                    }
                }
                while (!used.Add(candidate));
            }

            result.Add(candidate);
        }

        return result;
    }

    /// <summary>Pattern text "x,y;x,y;…" — also accepts spaces/newlines between cells.</summary>
    public static bool TryParsePattern(string? text, out IReadOnlyList<OverlayCell> cells)
    {
        var parsed = new List<OverlayCell>();
        cells = parsed;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var entry in text.Split(new[] { ';', '\n', '\r', ' ' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(',');
            if (parts.Length != 2
                || !int.TryParse(parts[0], out var x)
                || !int.TryParse(parts[1], out var y)
                || x < 0 || y < 0 || x > 24 || y > 24)
            {
                return false;
            }

            parsed.Add(new OverlayCell(x, y));
        }

        if (parsed.Count == 0)
        {
            return false;
        }

        cells = parsed;
        return true;
    }

    public static string FormatPattern(IReadOnlyList<OverlayCell> cells) =>
        string.Join(";", cells.Select(c => $"{c.X},{c.Y}"));

    private static void AppendRepeated(
        List<OverlayCell> cells, int count, IReadOnlyList<OverlayCell> pattern)
    {
        var stride = pattern.Max(c => c.X) + 2; // block width + one-cell gap
        var block = 0;
        while (cells.Count < count)
        {
            foreach (var cell in pattern)
            {
                if (cells.Count >= count)
                {
                    break;
                }

                cells.Add(new OverlayCell(cell.X + block * stride, cell.Y));
            }

            block++;
        }
    }
}
