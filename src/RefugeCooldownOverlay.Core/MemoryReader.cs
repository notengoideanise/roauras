using System.Security.Cryptography;

using System.Runtime.InteropServices;

namespace RefugeCooldownOverlay.Core;

/// <summary>Bounded 32-bit memory read abstraction. Synthetic tests supply fake images.</summary>
public interface ITimerMemory
{
    bool TryReadUInt32(nuint address, out uint value);
}

/// <summary>
/// Pure, portable identity comparison between a process's main module path and the
/// on-disk executable whose digest passed the exact-hash gate. Used so any current
/// or future patch executable name (PRM.exe, PRM-patch16.exe, ...) is accepted by
/// path identity rather than a fragile module-name allowlist.
/// </summary>
public static class ModuleIdentity
{
    public static bool MatchesExecutablePath(string? modulePath, string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(modulePath) || string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        static string Normalize(string path)
        {
            var full = Path.GetFullPath(path.Trim());
            return full.Replace('/', '\\').TrimEnd('\\');
        }

        return string.Equals(
            Normalize(modulePath), Normalize(executablePath), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Windows process reader. Opens query/read access only; performs no writes,
/// injection, hooks, packet access, or input generation. The executable digest is
/// verified against the exact profile registry BEFORE the process is opened.
/// </summary>
public sealed class ProcessTimerMemory : ITimerMemory, IDisposable
{
    private const int PROCESS_QUERY_INFORMATION = 0x0400;
    private const int PROCESS_VM_READ = 0x0010;

    private IntPtr _handle;

    public ProcessProfile Profile { get; }
    public nuint ModuleBase { get; }

    private ProcessTimerMemory(IntPtr handle, ProcessProfile profile, nuint moduleBase, string sha256)
    {
        _handle = handle;
        Profile = profile;
        ModuleBase = moduleBase;
        Sha256 = sha256;
    }

    /// <summary>Exact digest of the attached executable, confirmed before OpenProcess.</summary>
    public string Sha256 { get; }

    /// <summary>
    /// Hash the on-disk executable first; a non-allowlisted digest stops here without
    /// opening the process or reading any candidate address.
    /// </summary>
    public static bool TryAttach(
        int processId,
        string executablePath,
        out ProcessTimerMemory? reader,
        out string error)
    {
        reader = null;
        string digest;
        try
        {
            digest = Sha256File(executablePath);
        }
        catch (Exception ex)
        {
            error = $"Cannot hash executable '{executablePath}': {ex.Message}";
            return false;
        }

        var profile = ProcessProfiles.TryGetByHash(digest);
        if (profile is null)
        {
            error =
                $"Unsupported PRM build (sha256 {digest}). Timer offsets require revalidation; "
                + "no process access was attempted.";
            return false;
        }

        var handle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, processId);
        if (handle == IntPtr.Zero)
        {
            error = "Read access denied; match the game's own elevation. No bypass is attempted.";
            return false;
        }

        if (!TryGetModuleBase(processId, executablePath, out var moduleBase, out error))
        {
            CloseHandle(handle);
            return false;
        }

        reader = new ProcessTimerMemory(handle, profile, moduleBase, digest);
        error = string.Empty;
        return true;
    }

    public bool TryReadUInt32(nuint address, out uint value)
    {
        value = 0;
        if (_handle == IntPtr.Zero || address < 0x10000)
        {
            return false;
        }

        var buffer = new byte[4];
        if (!ReadProcessMemory(_handle, address, buffer, (nuint)buffer.Length, out var read)
            || read != (nuint)buffer.Length)
        {
            return false;
        }

        value = BitConverter.ToUInt32(buffer, 0);
        return true;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }

    ~ProcessTimerMemory() => Dispose();

    /// <summary>SHA-256 of a file, used by the controller to reconfirm the executable at poll boundaries.</summary>
    public static string HashFile(string path) => Sha256File(path);

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
    private static bool TryGetModuleBase(
        int processId, string executablePath, out nuint moduleBase, out string error)
    {
        moduleBase = 0;
        var snapshot = CreateToolhelp32Snapshot(0x18 /* TH32CS_SNAPMODULE */, (uint)processId);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            error = "Cannot snapshot modules to locate main module base.";
            return false;
        }

        try
        {
            var entry = new MODULEENTRY32W { dwSize = (uint)Marshal.SizeOf<MODULEENTRY32W>() };
            if (!Module32FirstW(snapshot, ref entry))
            {
                error = "Cannot read main module entry.";
                return false;
            }

            // The main module may be named PRM.exe, PRM-patch16.exe, or any future
            // patch name. Do not gate on the module NAME; compare the module's full
            // executable path to the exact path that passed the hash gate.
            var modulePath = entry.szExePath ?? string.Empty;
            if (!ModuleIdentity.MatchesExecutablePath(modulePath, executablePath))
            {
                error =
                    $"Main module path '{modulePath}' does not match hashed executable "
                    + $"'{executablePath}'; refusing to read.";
                return false;
            }

            moduleBase = entry.modBaseAddr;
            error = string.Empty;
            return true;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MODULEENTRY32W
    {
        public uint dwSize;
        public uint th32ModuleID;
        public uint th32ProcessID;
        public uint GlblcntUsage;
        public uint ProccntUsage;
        public nuint modBaseAddr;
        public uint modBaseSize;
        public IntPtr hModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExePath;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr process, nuint baseAddress, byte[] buffer, nuint size, out nuint read);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Module32FirstW(IntPtr snapshot, ref MODULEENTRY32W entry);
}
