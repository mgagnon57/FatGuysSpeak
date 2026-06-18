#if WINDOWS
using System.Runtime.InteropServices;
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Client.Services;

/// <summary>Injects a controller's RemoteInputDto into the local machine via Win32 SendInput.
/// Only the sharer uses this, and only while a control session is active.</summary>
public sealed class RemoteInputService(ScreenStreamService screen)
{
    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int cb);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int n);
    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public void Inject(RemoteInputDto dto)
    {
        try
        {
            switch (dto.Kind)
            {
                case RemoteInputKind.Move: SendMouse(dto, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE); break;
                case RemoteInputKind.Down: SendMouse(dto, ButtonFlag(dto.Button, down: true) | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE); break;
                case RemoteInputKind.Up:   SendMouse(dto, ButtonFlag(dto.Button, down: false) | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE); break;
                case RemoteInputKind.Wheel: SendWheel(dto.Delta); break;
                case RemoteInputKind.KeyDown: SendKey((ushort)dto.KeyCode, up: false); break;
                case RemoteInputKind.KeyUp:   SendKey((ushort)dto.KeyCode, up: true); break;
            }
        }
        catch { /* never let a bad input event break the stream */ }
    }

    private static uint ButtonFlag(int button, bool down) => button switch
    {
        1 => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
        2 => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
        _ => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
    };

    private void SendMouse(RemoteInputDto dto, uint flags)
    {
        var rect = screen.CurrentCaptureRect;
        var (px, py) = RemoteInputMapper.ToScreenPixel(dto.X, dto.Y, rect.Left, rect.Top, rect.Width, rect.Height);
        int sw = Math.Max(1, GetSystemMetrics(SM_CXSCREEN));
        int sh = Math.Max(1, GetSystemMetrics(SM_CYSCREEN));
        int ax = (int)(px * 65535.0 / sw);
        int ay = (int)(py * 65535.0 / sh);
        var input = new INPUT { type = INPUT_MOUSE, U = { mi = new MOUSEINPUT { dx = ax, dy = ay, dwFlags = flags } } };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private void SendWheel(int delta)
    {
        var input = new INPUT { type = INPUT_MOUSE, U = { mi = new MOUSEINPUT { mouseData = (uint)delta, dwFlags = MOUSEEVENTF_WHEEL } } };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private void SendKey(ushort vk, bool up)
    {
        var input = new INPUT { type = INPUT_KEYBOARD, U = { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } } };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }
}
#else
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Client.Services;

public sealed class RemoteInputService(ScreenStreamService screen)
{
    public void Inject(RemoteInputDto dto) { _ = screen; }
}
#endif
