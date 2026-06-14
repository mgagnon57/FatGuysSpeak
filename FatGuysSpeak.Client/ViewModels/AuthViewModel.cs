using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FatGuysSpeak.Client.Services;
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Client.ViewModels;

public partial class AuthViewModel(ApiService api, ChatHubService hub, PttService ptt) : ObservableObject
{
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _serverUrl = Preferences.Get("server_url", ApiService.DefaultServerUrl);

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
            api.SetCurrentUser(result.UserId, result.Username);
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
            api.SetCurrentUser(result.UserId, result.Username);
            ptt.LoadForUser(result.UserId);
            await hub.ConnectAsync(result.Token, api.ServerUrl);
            await Shell.Current.GoToAsync("//main");
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsLoading = false; }
    }
}
