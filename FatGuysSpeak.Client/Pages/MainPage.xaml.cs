using FatGuysSpeak.Client.ViewModels;
using Microsoft.Maui.Controls;

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

        // ViewModel fires this when a new message arrives and user is already at the bottom
        _vm.ScrollToLatestRequested += ScrollToBottom;

        // When the Messages collection is replaced (tab switch / channel change), fade the list
        // in and re-scroll to bottom — a soft cross-fade so switching channels "flows".
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_vm.Messages))
            {
                MessagesView.Opacity = 0;
                Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(80), () =>
                {
                    ScrollToBottom();
                    MessagesView.FadeTo(1, 220, Easing.CubicOut);
                });
            }
        };

        await _vm.LoadServersAsync();
        if (_vm.Servers.Count > 0)
        {
            await _vm.SelectServerAsync(_vm.Servers[0]);
            var lobby = _vm.Channels.FirstOrDefault(c => c.Channel.Name == "lobby")
                        ?? _vm.Channels.FirstOrDefault();
            if (lobby is not null)
                await _vm.SelectChannelAsync(lobby);
        }
    }

    private void ScrollToBottom()
    {
        if (_vm.Messages.Count > 0)
            MessagesView.ScrollTo(_vm.Messages[^1], animate: false);
    }

    private void OnMessagesScrolled(object? sender, ItemsViewScrolledEventArgs e)
    {
        _vm.OnScrollPositionChanged(e.LastVisibleItemIndex);
    }
}
