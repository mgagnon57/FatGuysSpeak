using System.Runtime.InteropServices;

namespace FatGuysSpeak.Client.Services;

public class PttService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL    = 14;
    private const int WM_KEYDOWN    = 0x0100;
    private const int WM_KEYUP      = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP   = 0x0105;
    // Mouse button messages (only the side + middle buttons are offered for PTT — binding left/right
    // would hijack normal clicking).
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP   = 0x0208;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_XBUTTONUP   = 0x020C;
    // Windows virtual-key codes for mouse buttons — reused so a mouse PTT slots into PttKey directly.
    private const int VK_MBUTTON  = 0x04;
    private const int VK_XBUTTON1 = 0x05;  // "Mouse 4" / back
    private const int VK_XBUTTON2 = 0x06;  // "Mouse 5" / forward

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    // Holds a reference so the delegate isn't GC'd while the hook is alive
    private readonly LowLevelKeyboardProc _proc;
    private readonly LowLevelKeyboardProc _mouseProc;
    private IntPtr _hookHandle;
    private IntPtr _mouseHook;
    private bool _isDown;
    private bool _isLearning;

    public int  PttKey     { get; private set; }
    public string PttKeyName => PttKey == 0 ? "(not set)" : VkToName(PttKey);

    public event Action?        PttDown;
    public event Action?        PttUp;
    public event Action<string>? KeyLearned; // fires with key name after rebind

    public bool IsHookInstalled => _hookHandle != IntPtr.Zero;

    private int _currentUserId;

    public PttService()
    {
        // Key is not loaded here — call LoadForUser(userId) after login
        _proc = HookCallback;
        _mouseProc = MouseHookCallback;

        // Only auto-install if the user has already consented (first run skips this).
        if (Preferences.Get("PttConsentGiven", false))
            TryInstall();
    }

    // hMod must be IntPtr.Zero for WH_KEYBOARD_LL — Windows ignores it for LL hooks.
    // Installing from the UI thread ensures the thread has a message pump to deliver callbacks.
    public void TryInstall()
    {
        if (_hookHandle != IntPtr.Zero) return;
        if (MainThread.IsMainThread)
            Install();
        else
            MainThread.BeginInvokeOnMainThread(Install);
    }

    private void Install()
    {
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
        _mouseHook  = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, IntPtr.Zero, 0);
    }

    public void BeginLearning()  => _isLearning = true;
    public void CancelLearning() => _isLearning = false;

    public void LoadForUser(int userId)
    {
        _currentUserId = userId;
        PttKey = Preferences.Get($"PttVirtualKey_{userId}", 0);
        _isDown = false;
    }

    public void ClearUser()
    {
        _currentUserId = 0;
        PttKey = 0;
        _isDown = false;
    }

    private void SetKey(int vk)
    {
        PttKey = vk;
        _isDown = false;
        if (_currentUserId != 0)
            Preferences.Set($"PttVirtualKey_{_currentUserId}", vk);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int  vk   = Marshal.ReadInt32(lParam);
            bool down = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
            bool up   = wParam == WM_KEYUP   || wParam == WM_SYSKEYUP;
            HandlePtt(vk, down, up);
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    // Low-level mouse hook: only the middle and side (X) buttons are eligible for PTT — left/right
    // are left alone so normal clicking still works.
    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            int vk = 0; bool down = false, up = false;
            switch (msg)
            {
                case WM_MBUTTONDOWN: vk = VK_MBUTTON; down = true; break;
                case WM_MBUTTONUP:   vk = VK_MBUTTON; up = true; break;
                case WM_XBUTTONDOWN:
                case WM_XBUTTONUP:
                    // mouseData hi-word says which X button: 1 = Mouse 4, 2 = Mouse 5.
                    int xbtn = (Marshal.ReadInt32(lParam, 8) >> 16) & 0xFFFF;
                    vk   = xbtn == 1 ? VK_XBUTTON1 : VK_XBUTTON2;
                    down = msg == WM_XBUTTONDOWN;
                    up   = msg == WM_XBUTTONUP;
                    break;
            }
            if (vk != 0) HandlePtt(vk, down, up);
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    // Shared by both hooks: learn a new binding while in learning mode, otherwise fire PTT down/up.
    private void HandlePtt(int vk, bool down, bool up)
    {
        if (_isLearning && down)
        {
            _isLearning = false;
            SetKey(vk);
            var name = PttKeyName;
            MainThread.BeginInvokeOnMainThread(() => KeyLearned?.Invoke(name));
        }
        else if (!_isLearning && PttKey != 0 && vk == PttKey)
        {
            if (down && !_isDown)
            {
                _isDown = true;
                MainThread.BeginInvokeOnMainThread(() => PttDown?.Invoke());
            }
            else if (up && _isDown)
            {
                _isDown = false;
                MainThread.BeginInvokeOnMainThread(() => PttUp?.Invoke());
            }
        }
    }

    public static string VkToName(int vk) => vk switch
    {
        >= 0x70 and <= 0x87              => $"F{vk - 0x6F}",
        >= 0x41 and <= 0x5A              => ((char)vk).ToString(),
        >= 0x30 and <= 0x39              => ((char)vk).ToString(),
        >= 0x60 and <= 0x69              => $"Num{vk - 0x60}",
        0x04 => "Middle Mouse", 0x05 => "Mouse 4",  0x06 => "Mouse 5",
        0x10 => "Shift",    0xA0 => "LShift",  0xA1 => "RShift",
        0x11 => "Ctrl",     0xA2 => "LCtrl",   0xA3 => "RCtrl",
        0x12 => "Alt",      0xA4 => "LAlt",    0xA5 => "RAlt",
        0x20 => "Space",    0x09 => "Tab",      0x14 => "CapsLock",
        0x2D => "Ins",      0x2E => "Del",
        0x24 => "Home",     0x23 => "End",
        0x21 => "PgUp",     0x22 => "PgDn",
        _    => $"0x{vk:X2}"
    };

    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }
}
