#if WINDOWS
using System.Windows;
using Microsoft.Web.WebView2.Wpf;

namespace FatGuysSpeak.Server.Dashboard;

public class DashboardWindow : Window
{
    public DashboardWindow()
    {
        Title = "FatGuysSpeak — Server Dashboard";
        Width = 1080;
        Height = 720;
        MinWidth = 820;
        MinHeight = 540;
        Background = System.Windows.Media.Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var webView = new WebView2();
        Content = webView;

        Loaded += async (_, _) =>
        {
            await webView.EnsureCoreWebView2Async();
            webView.Source = new Uri("http://localhost:5238/dashboard");
        };
    }
}
#endif
