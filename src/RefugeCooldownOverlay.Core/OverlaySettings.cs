using System.Text.Json;
using System.Text.Json.Serialization;

namespace RefugeCooldownOverlay.Core;

public enum OverlayBackdropStyle
{
    None,
    Dark,
    White,
}


/// <summary>
/// Local overlay settings. Stores skill slugs, never raw timer keys.
/// JSON is camelCase on disk (matching the shipped data/overlay-settings.json
/// template and user edits); loading is case-insensitive so either casing works.
/// </summary>
public sealed class OverlaySettings
{
    public static readonly JsonSerializerOptions SaveOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static readonly JsonSerializerOptions LoadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
    public List<string> SelectedSkillSlugs { get; set; } = new();

    // Overlay anchor as an OFFSET from the PRM window's top-left, so window
    // tracking keeps the placement across window moves.
    public int AnchorX { get; set; } = 40;
    public int AnchorY { get; set; } = 40;
    public int IconSize { get; set; } = 48;
    public int Columns { get; set; } = 2;

    /// <summary>Grid rows; 0 = auto-grow to fit every selected skill.</summary>
    public int Rows { get; set; } = 0;

    /// <summary>Tile arrangement preset; non-Grid presets ignore Columns/Rows.</summary>
    public OverlayLayoutPreset LayoutPreset { get; set; } = OverlayLayoutPreset.Grid;

    /// <summary>Occupied-cell pattern used when LayoutPreset is Custom; repeated side by side.</summary>
    public List<OverlayCell> CustomCells { get; set; } = new();
    public int Spacing { get; set; } = 6;
    public OverlayBackdropStyle Backdrop { get; set; } = OverlayBackdropStyle.Dark;
    public int BackdropPadding { get; set; } = 6;
    public double Opacity { get; set; } = 0.9;
    public bool ClickThrough { get; set; } = true;
    public int PollMilliseconds { get; set; } = 50;

    public string ProcessName { get; set; } = "PRM";

    public static OverlaySettings Load(string path)
    {
        try
        {
            var loaded = JsonSerializer.Deserialize<OverlaySettings>(
                File.ReadAllText(path), LoadOptions);
            if (loaded is null)
            {
                return new OverlaySettings();
            }

            loaded.PollMilliseconds = Math.Clamp(loaded.PollMilliseconds, 25, 1000);
            loaded.Columns = Math.Clamp(loaded.Columns, 1, 12);
            loaded.Rows = Math.Clamp(loaded.Rows, 0, 12);
            loaded.IconSize = Math.Clamp(loaded.IconSize, 16, 128);
            loaded.Opacity = Math.Clamp(loaded.Opacity, 0.2, 1.0);
            return loaded;
        }
        catch
        {
            // Malformed settings fall back to safe defaults; never crash the overlay.
            return new OverlaySettings();
        }
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(this, SaveOptions));
    }
}
