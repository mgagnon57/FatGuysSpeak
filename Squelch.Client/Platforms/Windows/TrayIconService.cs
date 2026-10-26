#if WINDOWS
using System.ComponentModel;
using System.Runtime.InteropServices;
using Squelch.Client.ViewModels;
using Squelch.Shared;

namespace Squelch.Client.Services;

/// <summary>Windows notification-area icon that mirrors voice state (idle / talking level / muted), built on
/// Win32 Shell_NotifyIcon with a hidden, never-shown top-level window created on the UI thread (WinUI's loop
/// pumps its messages). A hidden top-level window rather than a message-only one so it receives the
/// "TaskbarCreated" broadcast (re-adds the icon after Explorer restarts) and can take foreground for the menu.
/// Only the in-memory mic level is read; no audio is recorded or stored.</summary>
public sealed class TrayIconService : IDisposable
{
    private const uint TrayId = 1;
    private const uint WM_TRAY = 0x8000 + 1;              // WM_APP + 1: shell icon callback
    private const uint WM_TRAY_REFRESH = 0x8000 + 2;      // WM_APP + 2: apply the pending icon on the UI thread
    private const int LevelTimerMs = 100;
    private const uint CmdOpen = 1, CmdMute = 2, CmdQuit = 3;

    private readonly AudioService _audio;
    private readonly TrayIconModel _model = new();
    private readonly object _lock = new();
    private readonly WndProc _wndProc;                    // kept alive: native code holds a pointer to it
    private readonly System.Threading.Timer _levelTimer;

    private IntPtr _hwnd;
    private uint _taskbarCreatedMsg;
    private bool _iconAdded;
    private bool _refreshPosted;
    private bool _disposed;
    private IntPtr _hIdle, _hMuted;
    private readonly IntPtr[] _hLevels = new IntPtr[8];
    private TrayIconChoice _shown = new(TrayVisual.Idle, 0);

    private MainViewModel? _vm;
    private bool _inVoice, _muted, _transmitting;
    private double _latestLevel;
    private bool _freshLevel;

    public TrayIconService(AudioService audio)
    {
        _audio = audio;
        _wndProc = WindowProc;
        _levelTimer = new System.Threading.Timer(_ => OnLevelTick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Create the icon (first call, UI thread) and bind it to the view model. Calling again with a new
    /// view model (after sign-out/in) rebinds; the same view model is a no-op.</summary>
    public void Start(MainViewModel vm)
    {
        lock (_lock)
        {
            if (_disposed || ReferenceEquals(_vm, vm)) return;
            if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
            else
            {
                _audio.MicLevelChanged += OnMicLevel;
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            }
            _vm = vm;
            _vm.PropertyChanged += OnVmPropertyChanged;
            _inVoice = vm.InVoice;
            _muted = vm.IsMuted;
            _transmitting = vm.IsPttActive;
            if (_hwnd == IntPtr.Zero) CreateWindowAndLoadIcons();
            UpdateLocked();
        }
        if (!_iconAdded) AddIcon();   // shell IPC: never under the lock
    }

    // ── state feed ───────────────────────────────────────────────────────────

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel vm) return;
        lock (_lock)
        {
            if (_disposed || !ReferenceEquals(vm, _vm)) return;
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.InVoice):     _inVoice = vm.InVoice; break;
                case nameof(MainViewModel.IsMuted):     _muted = vm.IsMuted; break;
                case nameof(MainViewModel.IsPttActive): _transmitting = vm.IsPttActive; break;
                default: return;
            }
            UpdateLocked();
        }
    }

    // Background capture thread, ~50/s.
    private void OnMicLevel(double level)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _latestLevel = level;
            _freshLevel = true;
            UpdateLocked();
        }
    }

    // While a Level frame is shown, keep re-feeding the model so a throttled Level->Idle is always delivered
    // even when no further mic events arrive.
    private void OnLevelTick()
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (!_freshLevel) _latestLevel = 0;
            _freshLevel = false;
            UpdateLocked();
        }
    }

    /// <summary>Runs on the capture thread, the timer threadpool or the UI thread. Only evaluates the model and
    /// records the choice; Shell_NotifyIcon is cross-process IPC that can block, so the icon swap is posted to the
    /// tray window and applied on the UI thread (coalesced: at most one refresh in flight).</summary>
    private void UpdateLocked()
    {
        double gate = _audio.NoiseGateEnabled ? _audio.NoiseGateThreshold : 0.01;
        var choice = _model.Update(_inVoice, _muted, _transmitting, _latestLevel, gate, Environment.TickCount64);
        if (choice is { } c)
        {
            _shown = c;
            if (!_refreshPosted && _hwnd != IntPtr.Zero)
                _refreshPosted = PostMessage(_hwnd, WM_TRAY_REFRESH, IntPtr.Zero, IntPtr.Zero);
        }
        _levelTimer.Change(_shown.Visual == TrayVisual.Level ? LevelTimerMs : Timeout.Infinite,
                           _shown.Visual == TrayVisual.Level ? LevelTimerMs : Timeout.Infinite);
    }

    // ── shell icon ───────────────────────────────────────────────────────────

    private void CreateWindowAndLoadIcons()
    {
        var hInst = GetModuleHandle(null);
        var cls = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInst,
            lpszClassName = "SquelchTrayWindow",
        };
        RegisterClassEx(ref cls);   // fails harmlessly if already registered
        _hwnd = CreateWindowEx(WS_EX_TOOLWINDOW, "SquelchTrayWindow", "Squelch Tray", WS_POPUP,
                               0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");

        int size = GetSystemMetrics(SM_CXSMICON);
        if (size <= 0) size = 16;
        string dir = Path.Combine(AppContext.BaseDirectory, "Resources", "Tray");
        IntPtr Load(string name) => LoadImage(IntPtr.Zero, Path.Combine(dir, name), IMAGE_ICON, size, size, LR_LOADFROMFILE);
        _hIdle = Load("tray-idle.ico");
        _hMuted = Load("tray-muted.ico");
        for (int i = 0; i < _hLevels.Length; i++) _hLevels[i] = Load($"tray-level-{i}.ico");
    }

    private NOTIFYICONDATA MakeData(uint flags)
    {
        var (icon, tip) = _shown.Visual switch
        {
            TrayVisual.Muted => (_hMuted, "Squelch — Muted"),
            TrayVisual.Level => (_hLevels[Math.Clamp(_shown.Level, 0, 7)], "Squelch — Talking"),
            _                => (_hIdle, "Squelch — Idle"),
        };
        return new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = TrayId,
            uFlags = flags,
            uCallbackMessage = WM_TRAY,
            hIcon = icon,
            szTip = tip,
            szInfo = "",
            szInfoTitle = "",
        };
    }

    // UI thread. Builds the data under the lock, calls the shell outside it.
    private void AddIcon()
    {
        NOTIFYICONDATA data;
        lock (_lock)
        {
            if (_disposed || _hwnd == IntPtr.Zero) return;
            data = MakeData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        }
        bool ok = Shell_NotifyIcon(NIM_ADD, ref data);
        bool orphaned;
        lock (_lock)
        {
            orphaned = ok && _disposed;   // Dispose ran on another thread (ProcessExit) meanwhile
            if (!_disposed) _iconAdded = ok;
        }
        if (orphaned) Shell_NotifyIcon(NIM_DELETE, ref data);
    }

    // UI thread, from the posted WM_TRAY_REFRESH. A refresh arriving after Dispose is a no-op.
    private void ApplyPendingIcon()
    {
        NOTIFYICONDATA data;
        lock (_lock)
        {
            _refreshPosted = false;
            if (_disposed || !_iconAdded) return;
            data = MakeData(NIF_ICON | NIF_TIP);
        }
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAY)
        {
            switch ((uint)(lParam.ToInt64() & 0xFFFF))
            {
                case WM_LBUTTONDBLCLK: ShowMainWindow(); break;
                case WM_RBUTTONUP:     ShowMenu(); break;
            }
            return IntPtr.Zero;
        }
        if (msg == WM_TRAY_REFRESH)
        {
            ApplyPendingIcon();
            return IntPtr.Zero;
        }
        if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
        {
            AddIcon();   // Explorer restarted: re-add
            return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        bool muted;
        lock (_lock) { if (_disposed) return; muted = _muted; }
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, MF_STRING, CmdOpen, "Open Squelch");
            AppendMenu(menu, MF_STRING, CmdMute, muted ? "Unmute" : "Mute");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, CmdQuit, "Quit");
            GetCursorPos(out var pt);
            SetForegroundWindow(_hwnd);   // so the menu dismisses when clicking elsewhere
            uint cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN,
                                      pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);
            switch (cmd)
            {
                case CmdOpen: ShowMainWindow(); break;
                case CmdMute:
                    MainThread.BeginInvokeOnMainThread(() => _vm?.ToggleMuteCommand.Execute(null));
                    break;
                case CmdQuit:
                    MainThread.BeginInvokeOnMainThread(Quit);
                    break;
            }
        }
        finally { DestroyMenu(menu); }
    }

    private static Microsoft.Maui.Controls.Window? FindMainWindow()
    {
        var windows = Microsoft.Maui.Controls.Application.Current?.Windows;
        if (windows is null || windows.Count == 0) return null;
        return windows.FirstOrDefault(w => w.Page is AppShell) ?? windows[0];
    }

    private static void ShowMainWindow()
    {
        if (FindMainWindow()?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(native);
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
        native.Activate();
    }

    /// <summary>Same path as the user closing the window: the MAUI window's Destroying handler disconnects the
    /// hub and disposes this service, then the app exits.</summary>
    private static void Quit()
    {
        var main = FindMainWindow();
        if (main is not null) Microsoft.Maui.Controls.Application.Current?.CloseWindow(main);
    }

    // ── teardown ─────────────────────────────────────────────────────────────

    private void OnProcessExit(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        bool removeIcon;
        IntPtr hwnd;
        IntPtr[] icons;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;          // any posted refresh / later callback is now a no-op
            _levelTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _levelTimer.Dispose();
            _audio.MicLevelChanged -= OnMicLevel;
            if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = null;
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;

            removeIcon = _iconAdded;
            _iconAdded = false;
            hwnd = _hwnd;
            _hwnd = IntPtr.Zero;
            icons = [_hIdle, _hMuted, .. _hLevels];
            _hIdle = _hMuted = IntPtr.Zero;
            Array.Clear(_hLevels);
        }

        // Shell IPC and handle cleanup outside the lock so the capture thread never waits on Explorer.
        if (removeIcon)
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = hwnd, uID = TrayId, szTip = "", szInfo = "", szInfoTitle = "",
            };
            Shell_NotifyIcon(NIM_DELETE, ref data);
        }
        foreach (var h in icons) if (h != IntPtr.Zero) DestroyIcon(h);
        if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);   // no-op off the owning thread (ProcessExit)
    }

    // ── Win32 ────────────────────────────────────────────────────────────────

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    private const uint WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205;
    private const uint WS_POPUP = 0x80000000, WS_EX_TOOLWINDOW = 0x80;
    private const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10;
    private const int SM_CXSMICON = 49, SW_RESTORE = 9;
    private const uint MF_STRING = 0, MF_SEPARATOR = 0x800;
    private const uint TPM_RIGHTBUTTON = 0x2, TPM_BOTTOMALIGN = 0x20, TPM_RETURNCMD = 0x100;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h,
                                                IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr inst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
}
#endif
