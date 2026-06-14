using System.Runtime.InteropServices;

namespace FatGuysSpeak.Client.Services;

public class PttService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN    = 0x0100;
    private const int WM_KEYUP      = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP   = 0x0105;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    // Holds a reference so the delegate isn't GC'd while the hook is alive
    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookHandle;
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

        // hMod must be IntPtr.Zero for WH_KEYBOARD_LL — Windows ignores it for LL hooks.
        // Installing from the UI thread ensures the thread has a message pump to deliver callbacks.
        if (MainThread.IsMainThread)
            Install();
        else
            MainThread.BeginInvokeOnMainThread(Install);
    }

    private void Install()
    {
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
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
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public static string VkToName(int vk) => vk switch
    {
        >= 0x70 and <= 0x87              => $"F{vk - 0x6F}",
        >= 0x41 and <= 0x5A              => ((char)vk).ToString(),
        >= 0x30 and <= 0x39              => ((char)vk).ToString(),
        >= 0x60 and <= 0x69              => $"Num{vk - 0x60}",
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
    }
}
