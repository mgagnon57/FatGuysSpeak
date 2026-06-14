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
        var window = new Window(new AppShell())
        {
            Width = 1000,
            Height = 900,
            MinimumWidth = 800,
            MinimumHeight = 750,
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
