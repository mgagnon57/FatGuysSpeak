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

    // ── Drag/drop diagnostics ── appends to %TEMP%\fgs-dnd.log so we can see which events fire
    // (the MAUI client's console output isn't captured anywhere).
    private static readonly string DndLogPath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fgs-dnd.log");
    private static void DndLog(string msg)
    {
        try { System.IO.File.AppendAllText(DndLogPath, $"{DateTime.Now:HH:mm:ss.fff}  {msg}{Environment.NewLine}"); }
        catch { /* never let logging break the UI */ }
    }

    // Drag a voice occupant: stamp the user's id into the OS drag payload so Windows accepts the
    // drop (a drag with no data package won't register a drop target on WinUI).
    private void OnOccupantDragStarting(object? sender, DragStartingEventArgs e)
    {
        var user = (sender as Element)?.BindingContext as FatGuysSpeak.Shared.UserDto;
        DndLog($"DragStarting  sender={sender?.GetType().Name}  user={user?.Username}({user?.Id})");
        if (user is not null) e.Data.Text = user.Id.ToString();
    }

    // Fires repeatedly while a drag hovers a channel row. Explicitly accept the operation so WinUI
    // treats the row as a valid drop target (without this, Drop often never fires).
    private void OnChannelDragOver(object? sender, DragEventArgs e)
    {
        // Accept the drag so WinUI will raise Drop on this row (the missing piece for MAUI DnD).
        e.AcceptedOperation = DataPackageOperation.Copy;
    }

    // Drop on a channel row: read the dragged user's id and ask the VM to move them.
    private async void OnChannelDrop(object? sender, DropEventArgs e)
    {
        var ch = (sender as Element)?.BindingContext as ChannelViewItem;
        string? text = null;
        try { text = await e.Data.GetTextAsync(); } catch (Exception ex) { DndLog($"Drop GetTextAsync threw {ex.GetType().Name}"); }
        DndLog($"Drop  channel={ch?.Channel.Name}  text={text}");
        if (ch is not null && int.TryParse(text, out var userId))
            await _vm.MoveUserToVoiceChannel(userId, ch.Channel.Id);
    }
}
