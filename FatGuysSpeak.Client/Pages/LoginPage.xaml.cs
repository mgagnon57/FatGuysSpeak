using FatGuysSpeak.Client.ViewModels;

namespace FatGuysSpeak.Client.Pages;

public partial class LoginPage : ContentPage
{
    public LoginPage(AuthViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }

    private async void OnRegisterTapped(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("//register");
    }
}
