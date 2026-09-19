using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace RoAuras.Updater;

internal static class Program
{
    private const string AppExe = "RoAuras.exe";
    private const string ManifestFile = "roauras-update.json";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    [STAThread]
    private static void Main()
    {
        var root = AppContext.BaseDirectory;
        var app = Path.Combine(root, AppExe);
        try
        {
            var config = LoadConfig(root);
            if (config is not null)
            {
                ApplyUpdate(root, config);
            }
        }
        catch
        {
            // Offline/update failure must not prevent the installed app from starting.
        }

        if (File.Exists(app))
        {
            Process.Start(new ProcessStartInfo(app) { WorkingDirectory = root, UseShellExecute = true });
        }
    }

    private static UpdateConfig? LoadConfig(string root)
    {
        var path = Path.Combine(root, ManifestFile);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<UpdateConfig>(File.ReadAllText(path));
    }

    private static void ApplyUpdate(string root, UpdateConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ManifestUrl)) return;
        using var response = Http.GetAsync(config.ManifestUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode) return;
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.PackageUrl)) return;
        if (Version.TryParse(config.Version, out var current) && Version.TryParse(manifest.Version, out var latest) && latest <= current) return;

        var temp = Path.Combine(Path.GetTempPath(), $"roauras-update-{Guid.NewGuid():N}.zip");
        try
        {
            File.WriteAllBytes(temp, Http.GetByteArrayAsync(manifest.PackageUrl).GetAwaiter().GetResult());
            if (!string.Equals(Hash(temp), manifest.Sha256, StringComparison.OrdinalIgnoreCase)) return;
            WaitForAppToExit(app: Path.Combine(root, AppExe));
            var extract = Path.Combine(Path.GetTempPath(), $"roauras-extract-{Guid.NewGuid():N}");
            Directory.CreateDirectory(extract);
            System.IO.Compression.ZipFile.ExtractToDirectory(temp, extract);
            var source = Directory.GetDirectories(extract).SingleOrDefault() ?? extract;
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                if (relative.Equals("overlay-settings.json", StringComparison.OrdinalIgnoreCase)
                    || relative.StartsWith("overlay.log", StringComparison.OrdinalIgnoreCase)) continue;
                var destination = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
            }
            File.WriteAllText(Path.Combine(root, ManifestFile), JsonSerializer.Serialize(new UpdateConfig { ManifestUrl = config.ManifestUrl, Version = manifest.Version }, new JsonSerializerOptions { WriteIndented = true }));
            Directory.Delete(extract, true);
        }
        finally { try { File.Delete(temp); } catch { } }
    }

    private static void WaitForAppToExit(string app)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(app)))
        {
            try { process.CloseMainWindow(); process.WaitForExit(5000); } catch { }
            finally { process.Dispose(); }
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private sealed record UpdateConfig
    {
        public string ManifestUrl { get; init; } = "";
        public string Version { get; init; } = "0.0.0";
    }

    private sealed record UpdateManifest(string Version, string PackageUrl, string Sha256);
}
