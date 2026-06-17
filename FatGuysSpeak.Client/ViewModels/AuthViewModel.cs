using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FatGuysSpeak.Client.Services;
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Client.ViewModels;

public partial class AuthViewModel(ApiService api, ChatHubService hub, PttService ptt, GoogleAuthService google) : ObservableObject
{
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isGoogleAvailable;
    [ObservableProperty] private string _serverUrl = Preferences.Get("server_url", ApiService.DefaultServerUrl);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSavedServers))]
    [NotifyPropertyChangedFor(nameof(ServerPickerItems))]
    [NotifyPropertyChangedFor(nameof(IsEnteringNewServer))]
    private List<string> _savedServers = LoadSavedServers();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnteringNewServer))]
    private string? _selectedServerOption = GetInitialServerOption();

    private const string AddNewOption = "+ Add new server...";

    public bool HasSavedServers => SavedServers.Count > 0;
    public bool IsEnteringNewServer => !HasSavedServers || SelectedServerOption == AddNewOption;
    public List<string> ServerPickerItems => [.. SavedServers, AddNewOption];

    partial void OnServerUrlChanged(string value) =>
        Preferences.Set("server_url", value.TrimEnd('/'));

    partial void OnSelectedServerOptionChanged(string? value)
    {
        if (value is not null && value != AddNewOption)
            ServerUrl = value;
    }

    private static List<string> LoadSavedServers()
    {
        var raw = Preferences.Get("saved_servers", "");
        return string.IsNullOrEmpty(raw) ? [] : [.. raw.Split('|').Where(s => !string.IsNullOrEmpty(s))];
    }

    private static string? GetInitialServerOption()
    {
        var servers = LoadSavedServers();
        if (servers.Count == 0) return null;
        var current = Preferences.Get("server_url", ApiService.DefaultServerUrl);
        return servers.Contains(current) ? current : servers[0];
    }

    private void PersistServer(string url)
    {
        if (!SavedServers.Contains(url))
        {
            SavedServers = [url, .. SavedServers];
            Preferences.Set("saved_servers", string.Join("|", SavedServers));
        }
        SelectedServerOption = url;
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        ErrorMessage = "";
        IsLoading = true;
        try
        {
            api.SetServerUrl(ServerUrl);
            var result = await api.LoginAsync(new LoginRequest(Username, Password));
            if (result is null) { ErrorMessage = "Login failed."; return; }

            api.SetToken(result.Token);
            api.SetCurrentUser(result.UserId, result.Username, result.AvatarUrl);
            PersistServer(ServerUrl);
            ptt.LoadForUser(result.UserId);
            await hub.ConnectAsync(result.Token, api.ServerUrl);
            await Shell.Current.GoToAsync("//main");
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task RegisterAsync()
    {
        ErrorMessage = "";
        IsLoading = true;
        try
        {
            api.SetServerUrl(ServerUrl);
            var result = await api.RegisterAsync(new RegisterRequest(Username, Password, Email));
            if (result is null) { ErrorMessage = "Registration failed."; return; }

            api.SetToken(result.Token);
            api.SetCurrentUser(result.UserId, result.Username, result.AvatarUrl);
            PersistServer(ServerUrl);
            ptt.LoadForUser(result.UserId);
            await hub.ConnectAsync(result.Token, api.ServerUrl);
            await Shell.Current.GoToAsync("//main");
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsLoading = false; }
    }

    private string? _googleClientId;

    public async Task CheckGoogleAvailabilityAsync()
    {
        if (!OperatingSystem.IsWindows()) { IsGoogleAvailable = false; return; }
        try
        {
            api.SetServerUrl(ServerUrl);
            var cfg = await api.GetGoogleConfigAsync();
            _googleClientId = cfg?.ClientId;
            IsGoogleAvailable = !string.IsNullOrWhiteSpace(_googleClientId);
        }
        catch { IsGoogleAvailable = false; }
    }

    [RelayCommand]
    private async Task GoogleSignInAsync()
    {
        ErrorMessage = "";
        IsLoading = true;
        try
        {
            api.SetServerUrl(ServerUrl);
            // Use the client id fetched during the availability check; fall back to a fresh
            // fetch if the page didn't run it (e.g. server changed since load).
            var clientId = _googleClientId;
            if (string.IsNullOrWhiteSpace(clientId))
                clientId = (await api.GetGoogleConfigAsync())?.ClientId;
            if (string.IsNullOrWhiteSpace(clientId))
            {
                ErrorMessage = "Google sign-in is not available on this server.";
                return;
            }

            var result = await google.SignInAsync(clientId);
            if (!result.Success)
            {
                ErrorMessage = result.Error ?? "Google sign-in failed.";
                return;
            }

            var auth = await api.ExchangeGoogleCodeAsync(
                new FatGuysSpeak.Shared.GoogleCodeExchangeRequest(result.Code!, result.CodeVerifier!, result.RedirectUri!));
            if (auth is null) { ErrorMessage = "Google sign-in failed."; return; }

            api.SetToken(auth.Token);
            api.SetCurrentUser(auth.UserId, auth.Username, auth.AvatarUrl);
            PersistServer(ServerUrl);
            ptt.LoadForUser(auth.UserId);
            await hub.ConnectAsync(auth.Token, api.ServerUrl);
            await Shell.Current.GoToAsync("//main");
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsLoading = false; }
    }
}
