using FatGuysSpeak.Client.ViewModels;

namespace FatGuysSpeak.Client.Pages;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel _vm;

    public MainPage(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _vm.Initialize();
        _vm.Messages.CollectionChanged += (_, _) => ScrollToBottom();
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_vm.Messages))
            {
                _vm.Messages.CollectionChanged += (_, _) => ScrollToBottom();
                // Defer until after the CollectionView has rendered the new item set
                Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(80), ScrollToBottom);
            }
        };

        await _vm.LoadServersAsync();
        if (_vm.Servers.Count > 0)
        {
            await _vm.SelectServerAsync(_vm.Servers[0]);
            var general = _vm.Channels.FirstOrDefault(c => c.Channel.Name == "general");
            if (general is not null)
                await _vm.SelectChannelAsync(general);
        }
    }

    private void ScrollToBottom()
    {
        if (_vm.Messages.Count > 0)
            MessagesView.ScrollTo(_vm.Messages[^1], animate: false);
    }
}
