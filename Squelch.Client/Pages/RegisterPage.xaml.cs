using Squelch.Client.ViewModels;

namespace Squelch.Client.Pages;

public partial class RegisterPage : ContentPage
{
    public RegisterPage(AuthViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }

    private async void OnLoginTapped(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("//login");
    }
}
