using RefugeCooldownOverlay.Core;

namespace RefugeCooldownOverlay.App;

/// <summary>
/// Applies the shared dark palette (OverlayTheme) to dialogs, menus, and
/// controls. Whole-app dark theme; no white defaults anywhere.
/// </summary>
internal static class DarkTheme
{
    public static Color ToColor((byte R, byte G, byte B) rgb) =>
        Color.FromArgb(rgb.R, rgb.G, rgb.B);

    /// <summary>Recursively apply dark surfaces and muted text to a control tree.</summary>
    public static void Apply(Control control)
    {
        control.BackColor = ToColor(OverlayTheme.Surface);
        control.ForeColor = ToColor(OverlayTheme.TextPrimary);

        foreach (Control child in control.Controls)
        {
            Apply(child);
        }

        switch (control)
        {
            case TextBox or ListControl:
                // Readable input surfaces slightly lighter than the dialog.
                control.BackColor = ToColor(OverlayTheme.SurfaceControl);
                control.ForeColor = ToColor(OverlayTheme.TextPrimary);
                break;
            case ButtonBase button:
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = ToColor(OverlayTheme.CellBorder);
                button.BackColor = ToColor(OverlayTheme.SurfaceControl);
                button.ForeColor = ToColor(OverlayTheme.TextPrimary);
                break;
            case Label:
                control.ForeColor = ToColor(OverlayTheme.TextPrimary);
                break;
        }
    }

    /// <summary>Dark renderer + palette for ContextMenuStrip / MenuStrip.</summary>
    public static void Apply(ToolStrip strip)
    {
        strip.BackColor = ToColor(OverlayTheme.Surface);
        strip.ForeColor = ToColor(OverlayTheme.TextPrimary);
        strip.Renderer = new DarkToolStripRenderer();
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        private static Color Surface => ToColor(OverlayTheme.Surface);
        private static Color Control => ToColor(OverlayTheme.SurfaceControl);
        private static Color Border => ToColor(OverlayTheme.CellBorder);
        private static Color Accent => ToColor(OverlayTheme.Accent);

        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Border;
        public override Color MenuItemSelected => Control;
        public override Color MenuItemSelectedGradientBegin => Control;
        public override Color MenuItemSelectedGradientEnd => Control;
        public override Color MenuItemPressedGradientBegin => Surface;
        public override Color MenuItemPressedGradientEnd => Surface;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
        public override Color CheckBackground => Accent;
        public override Color CheckSelectedBackground => Accent;
        public override Color CheckPressedBackground => Accent;
        public override Color MenuStripGradientBegin => Surface;
        public override Color MenuStripGradientEnd => Surface;
    }

    private sealed class DarkToolStripRenderer : ToolStripProfessionalRenderer
    {
        public DarkToolStripRenderer() : base(new DarkColorTable())
        {
        }
    }
}
