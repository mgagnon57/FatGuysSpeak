namespace FatGuysSpeak.Client.Pages;

/// <summary>0.13.2 bridge screen: hands the installed FatGuysSpeak client over to Squelch.
/// Shown instead of AppShell only when Velopack-installed (see App.CreateWindow).</summary>
public partial class HandoverPage : ContentPage
{
    private bool _started;

    public HandoverPage()
    {
        InitializeComponent();
#if WINDOWS
        SetupLink.Text = SquelchHandover.SetupUrl;
#endif
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_started) return;
        _started = true;
#if WINDOWS
        var status = new Progress<string>(s => StatusLabel.Text = s);   // created on the UI thread
        HandoverResult result;
        try { result = await SquelchHandover.RunAsync(status, CancellationToken.None); }
        catch (Exception ex) { result = new HandoverResult(false, ex.Message); }

        if (!result.Success)
        {
            ShowFailure(result.Error ?? "Something went wrong moving you to Squelch.");
            return;
        }

        StatusLabel.Text = "Removing FatGuysSpeak…";
        // Squelch is installed and open; removal is best-effort (Update.exe stops this process).
        SquelchHandover.StartUninstall();
        await Task.Delay(500);   // let the status line paint before the window goes away
        Application.Current?.Quit();
#endif
    }

    private void ShowFailure(string error)
    {
        ProgressRow.IsVisible = false;
        ErrorLabel.Text = error;
        FailurePanel.IsVisible = true;
    }

    private async void OnSetupLinkTapped(object? sender, TappedEventArgs e)
    {
        try { await Launcher.OpenAsync(SetupLink.Text); } catch { }
    }

    private void OnKeepUsingClicked(object? sender, EventArgs e)
    {
        var window = Window ?? Application.Current?.Windows.FirstOrDefault();
        if (window is not null) window.Page = new AppShell();
    }
}
