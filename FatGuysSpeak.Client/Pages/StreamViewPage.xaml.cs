using FatGuysSpeak.Client.ViewModels;
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Client.Pages;

public partial class StreamViewPage : ContentPage
{
    public StreamViewPage()
    {
        InitializeComponent();
    }

#if WINDOWS
    // Throttle: send Move events at ~40/s (25ms minimum gap)
    private DateTime _lastMove = DateTime.MinValue;
    private const double MoveCooldownMs = 25;

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (Handler is null) return;

        // Wire pointer events on the stream image's native WinUI element
        var nativeImage = StreamImage.Handler?.PlatformView as Microsoft.UI.Xaml.FrameworkElement;
        if (nativeImage is not null)
        {
            nativeImage.PointerMoved   += OnNativePointerMoved;
            nativeImage.PointerPressed += OnNativePointerPressed;
            nativeImage.PointerReleased += OnNativePointerReleased;
            nativeImage.PointerWheelChanged += OnNativePointerWheel;
        }

        // Wire key events on the page root
        var pageRoot = Handler?.PlatformView as Microsoft.UI.Xaml.FrameworkElement;
        if (pageRoot is not null)
        {
            pageRoot.KeyDown += OnNativeKeyDown;
            pageRoot.KeyUp   += OnNativeKeyUp;

            // Panic hotkey: Ctrl+Alt+Break (Pause) → StopControlCommand
            var accel = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
            {
                Key = Windows.System.VirtualKey.Pause,
                Modifiers = Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Menu
            };
            accel.Invoked += (_, _) =>
            {
                if (BindingContext is MainViewModel vm)
                    vm.StopControlCommand.Execute(null);
            };
            pageRoot.KeyboardAccelerators.Add(accel);
        }
    }

    private MainViewModel? GetVm() => BindingContext as MainViewModel;

    private (double normX, double normY) Normalize(Windows.Foundation.Point pos,
        Microsoft.UI.Xaml.FrameworkElement element)
    {
        double w = Math.Max(1, element.ActualWidth);
        double h = Math.Max(1, element.ActualHeight);
        return (Math.Clamp(pos.X / w, 0, 1), Math.Clamp(pos.Y / h, 0, 1));
    }

    private void OnNativePointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var vm = GetVm();
        if (vm is null || !vm.IsControlling) return;

        var now = DateTime.UtcNow;
        if ((now - _lastMove).TotalMilliseconds < MoveCooldownMs) return;
        _lastMove = now;

        var el = (Microsoft.UI.Xaml.FrameworkElement)sender;
        var pt = e.GetCurrentPoint(el);
        var (nx, ny) = Normalize(pt.Position, el);
        vm.SendRemoteInput(new RemoteInputDto(RemoteInputKind.Move, nx, ny, 0, 0, 0));
    }

    private void OnNativePointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var vm = GetVm();
        if (vm is null || !vm.IsControlling) return;

        var el = (Microsoft.UI.Xaml.FrameworkElement)sender;
        var pt = e.GetCurrentPoint(el);
        var (nx, ny) = Normalize(pt.Position, el);
        int btn = pt.Properties.IsRightButtonPressed ? 1
                : pt.Properties.IsMiddleButtonPressed ? 2 : 0;
        vm.SendRemoteInput(new RemoteInputDto(RemoteInputKind.Down, nx, ny, btn, 0, 0));
    }

    private void OnNativePointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var vm = GetVm();
        if (vm is null || !vm.IsControlling) return;

        var el = (Microsoft.UI.Xaml.FrameworkElement)sender;
        var pt = e.GetCurrentPoint(el);
        var (nx, ny) = Normalize(pt.Position, el);
        // On release, check prior pressed state via pointer point properties
        int btn = pt.Properties.IsRightButtonPressed ? 1
                : pt.Properties.IsMiddleButtonPressed ? 2 : 0;
        vm.SendRemoteInput(new RemoteInputDto(RemoteInputKind.Up, nx, ny, btn, 0, 0));
    }

    private void OnNativePointerWheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var vm = GetVm();
        if (vm is null || !vm.IsControlling) return;

        var el = (Microsoft.UI.Xaml.FrameworkElement)sender;
        var pt = e.GetCurrentPoint(el);
        int delta = pt.Properties.MouseWheelDelta;
        var (nx, ny) = Normalize(pt.Position, el);
        vm.SendRemoteInput(new RemoteInputDto(RemoteInputKind.Wheel, nx, ny, 0, delta, 0));
    }

    private void OnNativeKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var vm = GetVm();
        if (vm is null || !vm.IsControlling) return;
        vm.SendRemoteInput(new RemoteInputDto(RemoteInputKind.KeyDown, 0, 0, 0, 0, (int)e.Key));
    }

    private void OnNativeKeyUp(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var vm = GetVm();
        if (vm is null || !vm.IsControlling) return;
        vm.SendRemoteInput(new RemoteInputDto(RemoteInputKind.KeyUp, 0, 0, 0, 0, (int)e.Key));
    }
#endif
}
