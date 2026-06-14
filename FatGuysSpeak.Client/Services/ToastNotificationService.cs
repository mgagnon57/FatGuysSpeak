#if WINDOWS
using System.Runtime.InteropServices;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace FatGuysSpeak.Client.Services;

public class ToastNotificationService
{
    private const string AppId = "FatGuysSpeak";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(string appId);

    public ToastNotificationService()
    {
        try { SetCurrentProcessExplicitAppUserModelID(AppId); }
        catch { /* unpackaged apps may not always succeed; toasts still work in most cases */ }
    }

    public void Show(string title, string body)
    {
        try
        {
            var xml = $"""
                <toast>
                  <visual>
                    <binding template="ToastGeneric">
                      <text>{Esc(title)}</text>
                      <text>{Esc(body)}</text>
                    </binding>
                  </visual>
                </toast>
                """;
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            ToastNotificationManager.CreateToastNotifier(AppId).Show(new ToastNotification(doc));
        }
        catch { /* never crash the app over a notification */ }
    }

    private static string Esc(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}

#else

namespace FatGuysSpeak.Client.Services;

public class ToastNotificationService
{
    public void Show(string title, string body) { }
}

#endif
