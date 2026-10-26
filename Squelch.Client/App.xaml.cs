using Squelch.Client.Services;

namespace Squelch.Client;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        ThemeService.ApplyStored();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        bool phoneMode = Environment.GetEnvironmentVariable("SQUELCH_PHONE_MODE") == "1";
        var window = new Window(new AppShell())
        {
            Width  = phoneMode ? 390 : 1000,
            Height = phoneMode ? 844 : 900,
            MinimumWidth  = phoneMode ? 360 : 800,
            MinimumHeight = phoneMode ? 640 : 750,
        };

        window.Destroying += async (_, _) =>
        {
#if WINDOWS
            // Remove the tray icon first: the process can exit while the hub disconnect is awaited.
            IPlatformApplication.Current?.Services.GetService<TrayIconService>()?.Dispose();
#endif
            var hub = IPlatformApplication.Current?.Services.GetService<ChatHubService>();
            if (hub is not null)
                await hub.DisconnectAsync();
        };

        // Track foreground state so background notifications only fire when the app isn't focused.
        window.Activated   += (_, _) => AppState.IsWindowActive = true;
        window.Deactivated += (_, _) => AppState.IsWindowActive = false;

        return window;
    }
}
