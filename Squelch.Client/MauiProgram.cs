using Squelch.Client.Pages;
using Squelch.Client.Services;
using Squelch.Client.ViewModels;
using Microsoft.Extensions.Logging;

namespace Squelch.Client;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        LegacySettingsMigration.RunOnce();   // before anything reads Preferences/SecureStorage
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
                fonts.AddFont("BebasNeue-Regular.ttf", "Bebas");
                fonts.AddFont("DMMono-Regular.ttf", "DMMono");
                fonts.AddFont("DMMono-Medium.ttf", "DMMonoMedium");
            });

        builder.Services.AddSingleton<ApiService>();
        builder.Services.AddSingleton<ChatHubService>();
        builder.Services.AddSingleton<AudioService>();
        builder.Services.AddSingleton<PttService>();
        builder.Services.AddSingleton<ScreenStreamService>();
        builder.Services.AddSingleton<GameWindowScanner>();
        builder.Services.AddSingleton<GameCaptureService>();
        builder.Services.AddSingleton<RemoteInputService>();
        builder.Services.AddSingleton<CameraService>();
        builder.Services.AddSingleton<ShareBorderOverlay>();
        builder.Services.AddSingleton<ToastNotificationService>();
        builder.Services.AddSingleton<GoogleAuthService>();
        builder.Services.AddSingleton<Squelch.Client.Services.UpdateService>();
#if WINDOWS
        builder.Services.AddSingleton<TrayIconService>();
#endif
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<PorkChopViewModel>();   // singleton: the ephemeral tab persists in-memory while the app runs

        builder.Services.AddTransient<AuthViewModel>();
        builder.Services.AddTransient<MainViewModel>();

        builder.Services.AddTransient<LoginPage>();
        builder.Services.AddTransient<RegisterPage>();
        builder.Services.AddTransient<MainPage>();

#if WINDOWS
        // Recolor the WinUI Entry focus underline (default is the system blue accent) to the live
        // theme accent. DynamicResource can't reach this WinUI-internal brush, so set it per control
        // and refresh it when the theme changes.
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("FgsAccentUnderline", (handler, view) =>
        {
            var tb = handler.PlatformView;
            void ApplyAccent()
            {
                var mc = ThemeService.Get("ThemeAccentLight");
                var c = global::Windows.UI.Color.FromArgb(
                    (byte)(mc.Alpha * 255), (byte)(mc.Red * 255), (byte)(mc.Green * 255), (byte)(mc.Blue * 255));
                var brush = new Microsoft.UI.Xaml.Media.SolidColorBrush(c);
                tb.Resources["TextControlBorderBrushFocused"] = brush;
            }
            ApplyAccent();
            void OnTheme() => tb.DispatcherQueue.TryEnqueue(ApplyAccent);
            ThemeService.ThemeChanged += OnTheme;
            tb.Unloaded += (_, _) => ThemeService.ThemeChanged -= OnTheme;
        });
#endif

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
