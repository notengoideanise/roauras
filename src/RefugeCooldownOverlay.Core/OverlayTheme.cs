namespace RefugeCooldownOverlay.Core;

/// <summary>
/// Dark-theme palette shared by the overlay and every dialog. Background tones
/// stay far from pure black so the overlay's transparency key never matches a
/// painted cell, and text tones are muted light — never pure white.
/// Values are plain RGB so offline tests can assert them without WinForms.
/// </summary>
public static class OverlayTheme
{
    public static readonly (byte R, byte G, byte B) ReadyCell = (34, 34, 40);
    public static readonly (byte R, byte G, byte B) CooldownCell = (24, 24, 30);
    public static readonly (byte R, byte G, byte B) UnavailableCell = (46, 26, 50);
    public static readonly (byte R, byte G, byte B) CellBorder = (66, 66, 74);
    public static readonly (byte R, byte G, byte B) Surface = (28, 28, 34);
    public static readonly (byte R, byte G, byte B) SurfaceControl = (40, 40, 48);
    public static readonly (byte R, byte G, byte B) TextPrimary = (226, 226, 232);
    public static readonly (byte R, byte G, byte B) TextDim = (164, 164, 174);
    public static readonly (byte R, byte G, byte B) TextOnCooldown = (150, 150, 160);
    public static readonly (byte R, byte G, byte B) Accent = (96, 84, 140);

    /// <summary>Background surfaces must stay dark (sum &lt;= 180) and never equal the transparency key.</summary>
    public static bool IsDarkBackground((byte R, byte G, byte B) rgb) =>
        rgb.R + rgb.G + rgb.B <= 180;

    /// <summary>Borders stay dark but lighter than fills (sum &lt;= 240).</summary>
    public static bool IsDarkBorder((byte R, byte G, byte B) rgb) =>
        rgb.R + rgb.G + rgb.B <= 240;

    /// <summary>Text must be readable but muted light: no channel at pure white (255).</summary>
    public static bool IsMutedLightText((byte R, byte G, byte B) rgb) =>
        rgb.R + rgb.G + rgb.B >= 330 && rgb.R < 255 && rgb.G < 255 && rgb.B < 255;

    public static IReadOnlyList<(byte R, byte G, byte B)> Backgrounds => new[]
    {
        ReadyCell, CooldownCell, UnavailableCell, Surface, SurfaceControl,
    };

    /// <summary>Borders sit slightly lighter than fills for visibility, but stay dark.</summary>
    public static IReadOnlyList<(byte R, byte G, byte B)> Borders => new[]
    {
        CellBorder,
    };

    public static IReadOnlyList<(byte R, byte G, byte B)> TextColors => new[]
    {
        TextPrimary, TextDim, TextOnCooldown,
    };
}

/// <summary>
/// Pure seam for the overlay's mouse pass-through decision: display mode passes
/// every hit-test through (WM_NCHITTEST → HTTRANSPARENT) so clicks land in the
/// game; configuration mode keeps the window interactive for dragging.
/// </summary>
public static class ClickThroughPolicy
{
    public static bool ShouldPassThrough(bool configurationActive) => !configurationActive;
}

/// <summary>Pure visibility rule: configuration may keep overlay visible while tray/config owns focus.</summary>
public static class OverlayVisibilityPolicy
{
    public static bool ShouldShow(bool configuring, bool targetUsable, bool targetForeground) =>
        targetUsable && (configuring || targetForeground);
}

/// <summary>
/// Render policy for the cooldown countdown label. Pure values so tests can
/// assert readability constraints without GDI+: the label must be a large
/// fraction of the tile, near-white with a near-black outline so it stays
/// readable over any icon art or backdrop.
/// </summary>
public static class CountdownStyle
{
    /// <summary>Font size as a fraction of the tile edge (0.x of icon size).</summary>
    public const float FontScale = 0.34f;

    /// <summary>Floor in points so small tiles still get a readable label.</summary>
    public const float MinimumFontPoints = 11f;

    /// <summary>Outline thickness in pixels applied as an 8-direction halo.</summary>
    public const int OutlineOffset = 2;

    /// <summary>Near-white fill — never pure white.</summary>
    public static readonly (byte R, byte G, byte B) Text = (240, 240, 246);

    /// <summary>Near-black halo behind the fill for contrast on any tile.</summary>
    public static readonly (byte R, byte G, byte B) Outline = (8, 8, 12);

    public static int TextBrightness() => Text.R + Text.G + Text.B;

    public static int OutlineBrightness() => Outline.R + Outline.G + Outline.B;

    /// <summary>Font size in points for a tile of the given edge length.</summary>
    public static float FontPoints(int iconSize) =>
        Math.Max(MinimumFontPoints, iconSize * FontScale);
}

/// <summary>
/// Backdrop policy: which panel color (if any), how much padding the panel
/// adds around the occupied grid, and the window corner radius. Pure seam so
/// tests can pin the contract — key rule: None adds no padding and paints no
/// panel, and the Dark panel is never the transparency-key pure black.
/// </summary>
public static class OverlayBackdropPolicy
{
    private static readonly (byte R, byte G, byte B) DarkPanel = (24, 24, 30);
    private static readonly (byte R, byte G, byte B) WhitePanel = (240, 240, 244);

    /// <summary>Panel color for the style, or null when no panel is painted.</summary>
    public static (byte R, byte G, byte B)? PanelColor(OverlayBackdropStyle style) => style switch
    {
        OverlayBackdropStyle.Dark => DarkPanel,
        OverlayBackdropStyle.White => WhitePanel,
        _ => null,
    };

    /// <summary>Padding around the occupied grid; zero in transparent mode.</summary>
    public static int Padding(OverlayBackdropStyle style, int requested) =>
        PanelColor(style) is null ? 0 : Math.Max(0, requested);

    /// <summary>Window corner radius; zero keeps the window rectangular.</summary>
    public static int CornerRadius(OverlayBackdropStyle style) =>
        PanelColor(style) is null ? 0 : 12;
}
