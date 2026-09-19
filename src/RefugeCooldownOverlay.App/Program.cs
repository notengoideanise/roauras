using RefugeCooldownOverlay.Core;

namespace RefugeCooldownOverlay.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var appDirectory = AppContext.BaseDirectory;
        var settingsPath = Path.Combine(appDirectory, "overlay-settings.json");
        var catalogPath = Path.Combine(appDirectory, "skill-catalog.json");
        var iconDirectory = Path.Combine(appDirectory, "icons");

        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RefugeCooldownOverlay");
        OverlayLog.InitPreferred(logDirectory, appDirectory);
        OverlayLog.Info("startup");

        // Diagnostics: unhandled UI/domain exceptions land in overlay.log, never
        // silently vanish; the overlay keeps running where safe.
        Application.ThreadException += (_, e) =>
            OverlayLog.Error($"UI exception: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            OverlayLog.Error($"unhandled: {e.ExceptionObject}");

        if (!File.Exists(settingsPath))
        {
            // First run: write a default settings template next to the executable.
            new OverlaySettings().Save(settingsPath);
        }

        var settings = OverlaySettings.Load(settingsPath);
        OverlayLog.Info(
            $"settings loaded: selected={settings.SelectedSkillSlugs.Count} columns={settings.Columns}"
            + $" rows={settings.Rows} iconSize={settings.IconSize} poll={settings.PollMilliseconds}");

        IReadOnlyList<SkillDefinition> catalog;
        try
        {
            var result = SkillCatalog.Load(catalogPath);
            catalog = result.Skills;
            OverlayLog.Info($"catalog loaded: {result.RowCount} rows");
        }
        catch (Exception ex)
        {
            OverlayLog.Error($"catalog load failed: {ex}");
            MessageBox.Show(
                $"Cannot load skill catalog '{catalogPath}': {ex.Message}\n\n"
                + "Copy data/skill-catalog.json and data/icons/ beside the executable.",
                "RoAuras",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        // Display order comes from the slug list order; unknown slugs are ignored.
        var selected = SkillCatalog.SelectInOrder(catalog, settings.SelectedSkillSlugs);

        var overlay = new OverlayForm(settings, iconDirectory);
        var configuring = false;
        ConfigurationForm? configuration = null;
        using var controller = new OverlayController(
            settings, overlay, selected, null,
            isConfiguring: () => configuring);

        // Configuration is created on demand and disposed on close, so
        // Configure → Save → Configure again can never touch a disposed form.
        void OpenConfiguration()
        {
            if (configuration is not null && !configuration.IsDisposed)
            {
                configuration.Activate();
                return;
            }

            OverlayLog.Info("configuration opened");
            configuration = new ConfigurationForm(
                settings,
                () => catalog,
                (updated, newSelection, persist) =>
                {
                    OverlayLog.Info($"settings applied: selected={newSelection.Count} persist={persist}");
                    overlay.Opacity = updated.Opacity;
                    overlay.ApplyBackdrop();
                    // Configuration owns click-through until the dialog closes;
                    // Apply must not silently re-enable pass-through and block dragging.
                    overlay.SetClickThrough(!configuring && updated.ClickThrough);
                    controller.SetSelectedSkills(newSelection);
                    controller.ApplyPollInterval(updated.PollMilliseconds);
                    if (persist)
                    {
                        updated.Save(settingsPath);
                    }
                });

            configuration.FormClosed += (_, _) =>
            {
                OverlayLog.Info("configuration closed");
                configuring = false;
                overlay.ConfigurationActive = false;
                overlay.SetClickThrough(settings.ClickThrough);
            };

            configuring = true;
            overlay.ConfigurationActive = true;
            overlay.SetClickThrough(false);
            configuration.Show();
            controller.RefreshNow();
        }

        // Drag end in configuration mode: save the overlay anchor as an offset
        // from the PRM window's top-left via AnchorMath — never screen coordinates.
        overlay.AnchorMoved += anchor =>
        {
            if (controller.TryGetTargetWindowRect(out var rect))
            {
                (settings.AnchorX, settings.AnchorY) = AnchorMath.AnchorFromScreen(
                    anchor.X, anchor.Y, rect.Left, rect.Top);
                settings.Save(settingsPath);
                OverlayLog.Info($"anchor saved: {settings.AnchorX},{settings.AnchorY} (PRM-relative)");
            }
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Configure RoAuras…", null, (_, _) => OpenConfiguration());
        menu.Items.Add("Open Log Folder", null, (_, _) =>
        {
            OverlayLog.Info("log folder opened");
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName = OverlayLog.DirectoryPath ?? appDirectory,
                    UseShellExecute = true,
                });
        });
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());
        DarkTheme.Apply(menu);

        var notify = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "RoAuras",
            ContextMenuStrip = menu,
            Visible = true,
        };

        OverlayLog.Info(
            $"selected mappings: {string.Join(", ", selected.Select(s => $"{s.Name}={s.RuntimeKey}:{s.MappingStatus}"))}");

        // The overlay only paints when PRM is foreground; start hidden so it can
        // never flash over other applications before attach.
        overlay.Visible = false;
        controller.Start();
        Application.Run();

        OverlayLog.Info("shutdown");
        notify.Dispose();
    }
}
