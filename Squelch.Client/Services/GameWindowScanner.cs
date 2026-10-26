#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
using Squelch.Shared;
using Microsoft.Win32;

namespace Squelch.Client.Services;

/// <summary>Gathers the Win32 facts GameDetector scores: visible windows (bounds, monitor, process) and the
/// .exe names found in installed game libraries. The library scan runs once in the background and is cached.</summary>
public sealed class GameWindowScanner
{
    private static readonly string[] GraphicsModules = ["d3d11.dll", "d3d12.dll", "dxgi.dll", "vulkan-1.dll", "opengl32.dll"];
    private readonly Lazy<Task<(IReadOnlySet<string> ExeNames, IReadOnlyList<string> Roots)>> _library;

    public GameWindowScanner()
    {
        _library = new(() => Task.Run(ScanLibraries));
        _ = _library.Value;   // warm the cache so the first Stream Game click doesn't wait on disk
    }

    public Task<(IReadOnlySet<string> ExeNames, IReadOnlyList<string> Roots)> GetLibraryAsync() => _library.Value;

    // ── Library scan ────────────────────────────────────────────────────────
    private static (IReadOnlySet<string>, IReadOnlyList<string>) ScanLibraries()
    {
        var roots = new List<string>();
        foreach (var steam in SteamInstallDirs())
        {
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            try { if (File.Exists(vdf)) roots.AddRange(SteamLibraryParser.ParseLibraryPaths(File.ReadAllText(vdf))); }
            catch { /* unreadable — skip */ }
        }
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            roots.AddRange(GameLibraryRoots.RelativeFolders.Select(rel => Path.Combine(drive.RootDirectory.FullName, rel)));

        var existing = roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true };
        foreach (var root in existing)
        {
            try
            {
                foreach (var exe in Directory.EnumerateFiles(root, "*.exe", options))
                    names.Add(Path.GetFileNameWithoutExtension(exe));
            }
            catch { /* a vanished/locked library shouldn't break detection */ }
        }
        return (names, existing);
    }

    private static IEnumerable<string> SteamInstallDirs()
    {
        var dirs = new List<string>();
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string p && p.Length > 0)
                dirs.Add(p.Replace('/', '\\'));
        }
        catch { }
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        return dirs.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    // ── Window facts ────────────────────────────────────────────────────────
    public IReadOnlyList<GameWindowFacts> GetWindowFacts()
    {
        var result = new List<GameWindowFacts>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            int len = GetWindowTextLength(hWnd);
            if (len == 0) return true;
            var sb = new System.Text.StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString().Trim();
            if (title.Length == 0) return true;

            GetWindowThreadProcessId(hWnd, out uint pid);
            string processName;
            try { processName = Process.GetProcessById((int)pid).ProcessName; }
            catch { return true; }

            GetWindowRect(hWnd, out var r);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST), ref mi);
            var m = mi.rcMonitor;

            result.Add(new GameWindowFacts(
                hWnd.ToInt64(), (int)pid, title, processName, TryGetExePath(pid),
                r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
                m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top,
                IsIconic(hWnd), TryUsesGraphics((int)pid)));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static string? TryGetExePath(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    private static bool? TryUsesGraphics(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            foreach (ProcessModule mod in p.Modules)
                if (GraphicsModules.Contains(mod.ModuleName, StringComparer.OrdinalIgnoreCase)) return true;
            return false;
        }
        catch { return null; }   // access denied (anti-cheat, elevated) — unknown, not "no"
    }

    // ── P/Invoke ────────────────────────────────────────────────────────────
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc fn, IntPtr lp);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder sb, int n);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr h, uint flags, System.Text.StringBuilder name, ref int size);

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
}
#else
using Squelch.Shared;

namespace Squelch.Client.Services;

public sealed class GameWindowScanner
{
    public Task<(IReadOnlySet<string> ExeNames, IReadOnlyList<string> Roots)> GetLibraryAsync() =>
        Task.FromResult<(IReadOnlySet<string>, IReadOnlyList<string>)>((new HashSet<string>(), []));
    public IReadOnlyList<GameWindowFacts> GetWindowFacts() => [];
}
#endif
