namespace FatGuysSpeak.Client.Services;

/// <summary>Lightweight app-wide UI state. <see cref="IsWindowActive"/> tracks whether the app
/// window is the foreground window, so background notifications only fire when the user isn't
/// already looking at the app.</summary>
public static class AppState
{
    public static bool IsWindowActive { get; set; } = true;
}
