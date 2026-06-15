using FatGuysSpeak.Client.Services;

namespace FatGuysSpeak.Client;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        bool phoneMode = Environment.GetEnvironmentVariable("FATGUYS_PHONE_MODE") == "1";
        var window = new Window(new AppShell())
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

        return window;
    }
}
