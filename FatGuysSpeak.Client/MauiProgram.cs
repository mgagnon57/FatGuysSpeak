using FatGuysSpeak.Client.Pages;
using FatGuysSpeak.Client.Services;
using FatGuysSpeak.Client.ViewModels;
using Microsoft.Extensions.Logging;

namespace FatGuysSpeak.Client;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
#if WINDOWS
        Velopack.VelopackApp.Build().Run();
#endif
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        builder.Services.AddSingleton<ApiService>();
        builder.Services.AddSingleton<ChatHubService>();
        builder.Services.AddSingleton<AudioService>();
        builder.Services.AddSingleton<SpeechService>();
        builder.Services.AddSingleton<PttService>();
        builder.Services.AddSingleton<ScreenStreamService>();
        builder.Services.AddSingleton<RemoteInputService>();
        builder.Services.AddSingleton<CameraService>();
        builder.Services.AddSingleton<ToastNotificationService>();
        builder.Services.AddSingleton<GoogleAuthService>();
        builder.Services.AddSingleton<FatGuysSpeak.Client.Services.UpdateService>();
        builder.Services.AddSingleton<SettingsViewModel>();

        builder.Services.AddTransient<AuthViewModel>();
        builder.Services.AddTransient<MainViewModel>();

        builder.Services.AddTransient<LoginPage>();
        builder.Services.AddTransient<RegisterPage>();
        builder.Services.AddTransient<MainPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
