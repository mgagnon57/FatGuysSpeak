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
    private long _lastMoveTicks;

    // Cached native elements for safe detach
    private Microsoft.UI.Xaml.FrameworkElement? _nativeImage;
    private Microsoft.UI.Xaml.FrameworkElement? _pageRoot;

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();

        if (Handler is null)
        {
            // Detach pointer handlers from the stream image
            if (_nativeImage is not null)
            {
                _nativeImage.PointerMoved        -= OnNativePointerMoved;
                _nativeImage.PointerPressed      -= OnNativePointerPressed;
                _nativeImage.PointerReleased     -= OnNativePointerReleased;
                _nativeImage.PointerWheelChanged -= OnNativePointerWheel;
                _nativeImage = null;
            }

            // Detach key handlers from the page root
            if (_pageRoot is not null)
            {
                _pageRoot.KeyDown -= OnNativeKeyDown;
                _pageRoot.KeyUp   -= OnNativeKeyUp;
                _pageRoot = null;
            }

            return;
        }

        // Guard: only wire once — if already wired just return
        if (_nativeImage is not null) return;

        // Wire pointer events on the stream image's native WinUI element
        var nativeImage = StreamImage.Handler?.PlatformView as Microsoft.UI.Xaml.FrameworkElement;
        if (nativeImage is not null)
        {
            nativeImage.PointerMoved        += OnNativePointerMoved;
            nativeImage.PointerPressed      += OnNativePointerPressed;
            nativeImage.PointerReleased     += OnNativePointerReleased;
            nativeImage.PointerWheelChanged += OnNativePointerWheel;
            _nativeImage = nativeImage;
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
                {
                    if (vm.IsControlling)
                        vm.ReleaseControlCommand.Execute(null);
                    else if (vm.IsBeingControlled)
                        vm.StopControlCommand.Execute(null);
                }
            };
            pageRoot.KeyboardAccelerators.Add(accel);
            _pageRoot = pageRoot;
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

        var now = Environment.TickCount64;
        if (now - _lastMoveTicks < 25) return;
        _lastMoveTicks = now;

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
        var kind = pt.Properties.PointerUpdateKind;
        int btn = kind == Microsoft.UI.Input.PointerUpdateKind.RightButtonPressed  ? 1
                : kind == Microsoft.UI.Input.PointerUpdateKind.MiddleButtonPressed ? 2 : 0;
        vm.SendRemoteInput(new RemoteInputDto(RemoteInputKind.Down, nx, ny, btn, 0, 0));
    }

    private void OnNativePointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var vm = GetVm();
        if (vm is null || !vm.IsControlling) return;

        var el = (Microsoft.UI.Xaml.FrameworkElement)sender;
        var pt = e.GetCurrentPoint(el);
        var (nx, ny) = Normalize(pt.Position, el);
        var kind = pt.Properties.PointerUpdateKind;
        int btn = kind == Microsoft.UI.Input.PointerUpdateKind.RightButtonReleased  ? 1
                : kind == Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased ? 2 : 0;
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
