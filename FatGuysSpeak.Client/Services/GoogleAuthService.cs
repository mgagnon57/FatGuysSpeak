using System.Net;
using System.Net.Sockets;
using System.Text;
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Client.Services;

public record GoogleCodeResult(bool Success, string? Code, string? CodeVerifier, string? RedirectUri, string? Error);

/// <summary>
/// Runs Google's loopback desktop OAuth flow: PKCE + system browser + a one-shot local
/// HttpListener that captures the redirect. Returns the auth code for the server to exchange.
/// Windows-only at runtime (gated by the caller).
/// </summary>
public class GoogleAuthService
{
    public async Task<GoogleCodeResult> SignInAsync(string clientId, CancellationToken ct = default)
    {
        var verifier = PkceHelper.GenerateVerifier();
        var challenge = PkceHelper.Challenge(verifier);
        var state = PkceHelper.GenerateState();

        var port = GetFreeLoopbackPort();
        var redirectUri = $"http://127.0.0.1:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();
        try
        {
            var url = GoogleAuthUrlBuilder.Build(clientId, redirectUri, challenge, state);
            await Launcher.Default.OpenAsync(url);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));

            var contextTask = listener.GetContextAsync();
            var finished = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, timeoutCts.Token));
            if (finished != contextTask)
                return new GoogleCodeResult(false, null, null, null, "Google sign-in was cancelled.");

            var context = await contextTask;
            var rawQuery = context.Request.Url?.Query ?? "";

            var html = "<html><body style='font-family:sans-serif;background:#1e1f22;color:#fff;text-align:center;padding-top:48px'>"
                     + "<h2>You can close this tab and return to FatGuysSpeak.</h2></body></html>";
            var buffer = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html";
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer, ct);
            context.Response.Close();

            var parsed = LoopbackRedirectParser.Parse(rawQuery, state);
            if (!parsed.Success)
            {
                var msg = parsed.Error == "access_denied"
                    ? "Google sign-in was cancelled."
                    : "Google sign-in failed.";
                return new GoogleCodeResult(false, null, null, null, msg);
            }
            return new GoogleCodeResult(true, parsed.Code, verifier, redirectUri, null);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static int GetFreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
