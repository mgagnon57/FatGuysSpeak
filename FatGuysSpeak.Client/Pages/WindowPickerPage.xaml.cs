using FatGuysSpeak.Client.ViewModels;

namespace FatGuysSpeak.Client.Pages;

public partial class WindowPickerPage : ContentPage
{
    public WindowPickerPage() => InitializeComponent();

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (BindingContext is WindowPickerViewModel vm)
            vm.Load();
    }
}
