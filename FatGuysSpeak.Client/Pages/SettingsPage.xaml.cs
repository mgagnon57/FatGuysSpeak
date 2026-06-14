using FatGuysSpeak.Client.ViewModels;

namespace FatGuysSpeak.Client.Pages;

public partial class SettingsPage : ContentPage
{
    public SettingsPage() => InitializeComponent();

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (BindingContext is SettingsViewModel vm) vm.Load();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (BindingContext is SettingsViewModel vm) vm.Unload();
    }

    private void OnCloseClicked(object sender, EventArgs e) =>
        Application.Current?.CloseWindow(Window!);
}
