using RefugeCooldownOverlay.Core;

namespace RefugeCooldownOverlay.App;

/// <summary>
/// Bounded custom-layout editor. Selected skills are draggable on a 12×12 cell
/// grid; positions are stored in selected-skill order and rendered by the overlay.
/// Empty cells are legal; duplicate occupied cells are not.
/// </summary>
public sealed class CustomLayoutEditor : Control
{
    private const int Bound = 12;
    private const int CellSize = 34;
    private IReadOnlyList<SkillDefinition> _selected = Array.Empty<SkillDefinition>();
    private readonly List<OverlayCell> _cells = new();
    private int _dragIndex = -1;
    private OverlayCell? _dropCell;

    public CustomLayoutEditor()
    {
        DoubleBuffered = true;
        Size = new Size(420, 420);
    }

    public IReadOnlyList<OverlayCell> Cells => OverlayPanel.ResolveCustom(_selected.Count, _cells, Bound);

    public void SetSelection(
        IReadOnlyList<SkillDefinition> selected,
        IReadOnlyList<OverlayCell> cells)
    {
        _selected = selected;
        _cells.Clear();
        _cells.AddRange(OverlayPanel.ResolveCustom(selected.Count, cells, Bound));
        Invalidate();
    }

    public void UpdateSelection(IReadOnlyList<SkillDefinition> selected) =>
        SetSelection(selected, OverlayPanel.ResolveCustom(selected.Count, _cells, Bound));

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        using var surface = new SolidBrush(DarkTheme.ToColor(OverlayTheme.SurfaceControl));
        using var grid = new Pen(DarkTheme.ToColor(OverlayTheme.CellBorder));
        using var hover = new SolidBrush(Color.FromArgb(80, OverlayTheme.Accent.R, OverlayTheme.Accent.G, OverlayTheme.Accent.B));
        using var text = new SolidBrush(DarkTheme.ToColor(OverlayTheme.TextPrimary));
        using var font = new Font(Font.FontFamily, 7f, FontStyle.Bold);
        graphics.FillRectangle(surface, ClientRectangle);

        for (var y = 0; y < Bound; y++)
        {
            for (var x = 0; x < Bound; x++)
            {
                graphics.DrawRectangle(grid, CellBounds(x, y));
            }
        }

        if (_dropCell is { } drop && _dropCell.Value.X >= 0 && _dropCell.Value.Y >= 0)
        {
            graphics.FillRectangle(hover, CellBounds(drop.X, drop.Y));
        }

        for (var index = 0; index < _selected.Count; index++)
        {
            var cell = _cells[index];
            var bounds = Rectangle.Inflate(CellBounds(cell.X, cell.Y), -2, -2);
            using var tile = new SolidBrush(DarkTheme.ToColor(OverlayTheme.Surface));
            graphics.FillRectangle(tile, bounds);
            graphics.DrawRectangle(grid, bounds);
            var label = SkillMonogram.For(_selected[index].Name);
            var size = graphics.MeasureString(label, font);
            graphics.DrawString(label, font, text,
                bounds.X + (bounds.Width - size.Width) / 2,
                bounds.Y + (bounds.Height - size.Height) / 2);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        _dragIndex = HitIndex(e.Location);
        _dropCell = ScreenToCell(e.Location);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragIndex >= 0)
        {
            _dropCell = ScreenToCell(e.Location);
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragIndex >= 0)
        {
            var target = ScreenToCell(e.Location);
            target = new OverlayCell(
                Math.Clamp(target.X, 0, Bound - 1),
                Math.Clamp(target.Y, 0, Bound - 1));
            var collision = Enumerable.Range(0, _cells.Count)
                .Where(index => index != _dragIndex && _cells[index] == target)
                .FirstOrDefault(-1);
            if (collision >= 0)
            {
                _cells[collision] = _cells[_dragIndex];
            }

            _cells[_dragIndex] = target;
        }

        _dragIndex = -1;
        _dropCell = null;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_dragIndex < 0)
        {
            _dropCell = null;
            Invalidate();
        }
    }

    private int HitIndex(Point point)
    {
        for (var index = _cells.Count - 1; index >= 0; index--)
        {
            var bounds = CellBounds(_cells[index].X, _cells[index].Y);
            if (bounds.Contains(point))
            {
                return index;
            }
        }

        return -1;
    }

    private OverlayCell ScreenToCell(Point point) =>
        new(Math.Clamp(point.X / CellSize, 0, Bound - 1),
            Math.Clamp(point.Y / CellSize, 0, Bound - 1));

    private Rectangle CellBounds(int x, int y) =>
        new(x * CellSize + 1, y * CellSize + 1, CellSize - 2, CellSize - 2);
}
