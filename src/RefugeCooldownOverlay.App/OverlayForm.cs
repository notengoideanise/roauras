using RefugeCooldownOverlay.Core;

namespace RefugeCooldownOverlay.App;

/// <summary>
/// Borderless layered overlay grid of skill icons. Render-only in display mode:
/// every hit-test is answered HTTRANSPARENT and mouse-activation is refused, so
/// clicks and focus always stay in the game. Configuration mode temporarily makes
/// the window interactive for dragging. All brushes are owned instances — shared
/// System.Drawing brushes must never be disposed (that caused the DrawString
/// ArgumentException regression).
/// </summary>
public sealed class OverlayForm : Form
{
    private IReadOnlyList<SkillCooldownState> _states = Array.Empty<SkillCooldownState>();
    private readonly OverlaySettings _settings;
    private readonly string _iconDirectory;
    private readonly Dictionary<string, Image?> _iconCache = new();

    public OverlayForm(OverlaySettings settings, string iconDirectory)
    {
        _settings = settings;
        _iconDirectory = iconDirectory;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true; // combined with foreground-only visibility: hidden whenever PRM is not foreground
        DoubleBuffered = true;
        ApplyBackdrop();
        Opacity = settings.Opacity;
        Location = new Point(settings.AnchorX, settings.AnchorY);
        HandleCreated += (_, _) => Win32Window.SetClickThrough(Handle, settings.ClickThrough);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= Win32Window.WS_EX_NOACTIVATE | Win32Window.WS_EX_TOOLWINDOW;
            return parameters;
        }
    }

    public void SetClickThrough(bool clickThrough)
    {
        if (IsHandleCreated)
        {
            Win32Window.SetClickThrough(Handle, clickThrough);
        }
    }

    public void ApplyBackdrop()
    {
        var panel = OverlayBackdropPolicy.PanelColor(_settings.Backdrop);
        if (panel is null)
        {
            BackColor = Color.Black;
            TransparencyKey = Color.Black;
        }
        else
        {
            TransparencyKey = Color.Empty;
            BackColor = Color.FromArgb(panel.Value.R, panel.Value.G, panel.Value.B);
        }

        ResizeToContent();
        Invalidate();
    }

    /// <summary>
    /// Display mode: every hit-test passes through (clicks land in the game) and
    /// mouse activation is refused so the overlay can never take focus.
    /// Configuration mode keeps normal hit-testing for dragging.
    /// </summary>
    protected override void WndProc(ref Message message)
    {
        const int WM_NCHITTEST = 0x0084;
        const int HTTRANSPARENT = -1;
        if (message.Msg == WM_NCHITTEST
            && ClickThroughPolicy.ShouldPassThrough(ConfigurationActive))
        {
            message.Result = (IntPtr)HTTRANSPARENT;
            return;
        }

        if (message.Msg == Win32Window.WM_MOUSEACTIVATE
            && ClickThroughPolicy.ShouldPassThrough(ConfigurationActive))
        {
            message.Result = (IntPtr)Win32Window.MA_NOACTIVATE;
            return;
        }

        base.WndProc(ref message);
    }

    /// <summary>
    /// Configuration mode: repositioning is paused and the overlay accepts mouse
    /// drag (click-through must be off). On drag end the new anchor is reported so
    /// the caller can persist it as an offset from the PRM window.
    /// </summary>
    public bool ConfigurationActive { get; set; }

    public event Action<Point>? AnchorMoved;

    private Point _dragStartLocation;
    private Point _dragStartMouse;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (ConfigurationActive)
        {
            _dragStartLocation = Location;
            _dragStartMouse = Cursor.Position;
            Capture = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (ConfigurationActive && Capture)
        {
            Location = new Point(
                _dragStartLocation.X + Cursor.Position.X - _dragStartMouse.X,
                _dragStartLocation.Y + Cursor.Position.Y - _dragStartMouse.Y);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (ConfigurationActive && Capture)
        {
            Capture = false;
            AnchorMoved?.Invoke(Location);
        }
    }

    public void UpdateStates(IReadOnlyList<SkillCooldownState> states)
    {
        _states = states;
        ResizeToContent();
        Invalidate();
    }

    private IReadOnlyList<OverlayCell> OccupiedCells() => OverlayPanel.CellsFor(
        _states.Count, _settings.Columns, _settings.Rows,
        _settings.LayoutPreset, _settings.CustomCells);

    private int CellPadding => OverlayBackdropPolicy.Padding(
        _settings.Backdrop, _settings.BackdropPadding);

    private void ResizeToContent()
    {
        // Panel hugs occupied tiles only — never reserved grid capacity.
        var (width, height) = OverlayPanel.Measure(
            OccupiedCells(), _settings.IconSize, _settings.Spacing, CellPadding);
        Size = new Size(width, height);
        UpdateWindowRegion();
    }

    private void UpdateWindowRegion()
    {
        if (_settings.Backdrop == OverlayBackdropStyle.None || Width < 2 || Height < 2)
        {
            Region?.Dispose();
            Region = null;
            return;
        }

        // Shape the window from occupied cells, not its rectangular bounds. A
        // 3+1 layout therefore has no painted/clickable backdrop in the two
        // unused cells of the final row.
        var cells = OccupiedCells();
        var step = _settings.IconSize + _settings.Spacing;
        var padding = CellPadding;
        var region = new Region();
        region.MakeEmpty();
        foreach (var cell in cells)
        {
            var tile = new Rectangle(
                Math.Max(0, cell.X * step),
                Math.Max(0, cell.Y * step),
                Math.Min(Width - cell.X * step, _settings.IconSize + padding * 2),
                Math.Min(Height - cell.Y * step, _settings.IconSize + padding * 2));
            region.Union(tile);
        }

        Region?.Dispose();
        Region = region;
    }

    private Image? IconFor(SkillCooldownState state)
    {
        var iconFile = state.Skill.IconFile;
        if (string.IsNullOrEmpty(iconFile))
        {
            return null;
        }

        if (_iconCache.TryGetValue(iconFile, out var cached))
        {
            return cached;
        }

        Image? image = null;
        try
        {
            var path = Path.Combine(_iconDirectory, iconFile);
            if (File.Exists(path))
            {
                // Load via a copy so the BMP file is never locked on disk.
                using var stream = new MemoryStream(File.ReadAllBytes(path));
                image = new Bitmap(stream);
            }
        }
        catch (Exception ex)
        {
            OverlayLog.Error($"icon load failed for {iconFile}: {ex.Message}");
            image = null;
        }

        _iconCache[iconFile] = image;
        return image;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

        var cells = OccupiedCells();
        var step = _settings.IconSize + _settings.Spacing;

        // Owned resources only — never dispose shared System.Drawing brushes.
        // State cues stay visible without burying client icon art.
        using var unavailableWash = new SolidBrush(Color.FromArgb(45, OverlayTheme.UnavailableCell.R, OverlayTheme.UnavailableCell.G, OverlayTheme.UnavailableCell.B));
        // Middle-strength cooldown cue: obvious state, readable client art.
        using var cooldownWash = new SolidBrush(Color.FromArgb(90, 10, 10, 14));
        using var borderPen = new Pen(ToColor(OverlayTheme.CellBorder));
        using var textBrush = ToBrush(OverlayTheme.TextPrimary);
        using var countdownFill = ToBrush(CountdownStyle.Text);
        using var countdownOutline = ToBrush(CountdownStyle.Outline);
        using var countFont = new Font(Font.FontFamily, CountdownStyle.FontPoints(_settings.IconSize), FontStyle.Bold);
        var padding = CellPadding;

        for (var index = 0; index < _states.Count && index < cells.Count; index++)
        {
            var state = _states[index];
            var place = cells[index];
            var cell = new Rectangle(
                place.X * step + padding, place.Y * step + padding,
                _settings.IconSize, _settings.IconSize);
            var icon = IconFor(state);

            if (icon is not null)
            {
                graphics.DrawImage(icon, cell);
            }
            else
            {
                // No client art: dark tile with a deterministic monogram, never fake art.
                using var fallback = new SolidBrush(Color.FromArgb(210, OverlayTheme.ReadyCell.R, OverlayTheme.ReadyCell.G, OverlayTheme.ReadyCell.B));
                graphics.FillRectangle(fallback, cell);
                var mono = SkillMonogram.For(state.Skill.Name);
                var monoSize = graphics.MeasureString(mono, countFont);
                graphics.DrawString(mono, countFont, textBrush,
                    cell.X + (cell.Width - monoSize.Width) / 2,
                    cell.Y + (cell.Height - monoSize.Height) / 2);
            }

            switch (state.State)
            {
                case CooldownDisplayState.Cooldown:
                {
                    // Dark sweep + centered live remaining countdown, driven only
                    // by the client's expiry tick. Large near-white digits with a
                    // dark halo stay readable over any icon art.
                    graphics.FillRectangle(cooldownWash, cell);
                    var label = CooldownFormat.Seconds(state.RemainingMilliseconds ?? 0);
                    var size = graphics.MeasureString(label, countFont);
                    var centerX = cell.X + (cell.Width - size.Width) / 2;
                    var centerY = cell.Y + (cell.Height - size.Height) / 2;
                    var halo = CountdownStyle.OutlineOffset;
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        for (var dy = -1; dy <= 1; dy++)
                        {
                            if (dx == 0 && dy == 0)
                            {
                                continue;
                            }

                            graphics.DrawString(label, countFont, countdownOutline,
                                centerX + dx * halo, centerY + dy * halo);
                        }
                    }

                    graphics.DrawString(label, countFont, countdownFill, centerX, centerY);
                    break;
                }
                case CooldownDisplayState.SamplingUnavailable:
                case CooldownDisplayState.MappingUnavailable:
                    // Sampling/mapping failures stay visibly distinct without text.
                    graphics.FillRectangle(unavailableWash, cell);
                    break;
                case CooldownDisplayState.NeedsValidation:
                    // Inferred idle skills remain bright; a missing observation is
                    // not evidence that the skill is unavailable or on cooldown.
                    break;
                case CooldownDisplayState.Ready:
                    // Full-brightness icon with border = ready. No text needed.
                    break;
            }

            graphics.DrawRectangle(borderPen, cell);
        }
    }

    private static SolidBrush ToBrush((byte R, byte G, byte B) rgb, int alpha = 255) =>
        new(Color.FromArgb(alpha, rgb.R, rgb.G, rgb.B));

    private static Color ToColor((byte R, byte G, byte B) rgb) =>
        Color.FromArgb(rgb.R, rgb.G, rgb.B);
}
