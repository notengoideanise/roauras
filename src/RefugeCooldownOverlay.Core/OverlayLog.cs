namespace RefugeCooldownOverlay.Core;

/// <summary>
/// Bounded rolling file logger. Writes <c>overlay.log</c> plus one
/// <c>overlay.log.1</c> backup; never exceeds CapBytes on the main file; never
/// throws into UI paths (uninitialized or IO failures are swallowed). Never log
/// raw memory, credentials, chat, account, or input data.
/// </summary>
public static class OverlayLog
{
    private static readonly object Gate = new();
    private static string? _directory;
    private static long _capBytes = 1024 * 1024;

    public static string? DirectoryPath
    {
        get { lock (Gate) return _directory; }
    }

    public static long CapBytes
    {
        get { lock (Gate) return _capBytes; }
        set { lock (Gate) _capBytes = Math.Max(256, value); }
    }

    public static void Init(string? directory) => InitPreferred(directory, null);

    public static void InitPreferred(string? preferred, string? fallback)
    {
        lock (Gate)
        {
            _directory = null;
            foreach (var candidate in new[] { preferred, fallback })
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(candidate);
                    var probe = Path.Combine(candidate, ".overlay-log-probe");
                    File.WriteAllText(probe, string.Empty);
                    File.Delete(probe);
                    _directory = candidate;
                    return;
                }
                catch
                {
                    // Try next location.
                }
            }
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void ResetForTests()
    {
        lock (Gate)
        {
            _directory = null;
            _capBytes = 1024 * 1024;
        }
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                if (_directory is null)
                {
                    return;
                }

                var path = Path.Combine(_directory, "overlay.log");
                var line = $"[{DateTime.Now:HH:mm:ss}] {level} {message}"
                    + Environment.NewLine;

                if (File.Exists(path) && new FileInfo(path).Length + line.Length > _capBytes)
                {
                    var backup = Path.Combine(_directory, "overlay.log.1");
                    if (File.Exists(backup))
                    {
                        File.Delete(backup);
                    }

                    File.Move(path, backup);
                }

                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Swallow: a broken log must never break the overlay.
        }
    }
}
