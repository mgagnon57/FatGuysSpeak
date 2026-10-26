using FatGuysSpeak.Client.Services;

namespace FatGuysSpeak.Client;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        ThemeService.ApplyStored();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        bool phoneMode = Environment.GetEnvironmentVariable("FATGUYS_PHONE_MODE") == "1";
        // 0.13.2 bridge: an installed FatGuysSpeak hands itself over to Squelch instead of opening
        // the normal shell. Dev / unpackaged runs (not Velopack-installed) go straight to AppShell.
#if WINDOWS
        Page firstPage = SquelchHandover.ShouldRun() ? new Pages.HandoverPage() : new AppShell();
#else
        Page firstPage = new AppShell();
#endif
        var window = new Window(firstPage)
        {
            Width  = phoneMode ? 390 : 1000,
            Height = phoneMode ? 844 : 900,
            MinimumWidth  = phoneMode ? 360 : 800,
            MinimumHeight = phoneMode ? 640 : 750,
        };

        window.Destroying += async (_, _) =>
        {
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
