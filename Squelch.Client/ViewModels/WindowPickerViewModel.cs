using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Squelch.Client.Models;
using Squelch.Client.Services;
using Squelch.Shared;

namespace Squelch.Client.ViewModels;

public partial class WindowPickerViewModel : ObservableObject
{
    [ObservableProperty] private List<AppWindow> _filteredWindows = [];
    [ObservableProperty] private AppWindow? _selectedWindow;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _heading = "Choose what to share";
    [ObservableProperty] private string _hint = "Select a window or Entire Screen to share";

    private List<AppWindow> _allWindows = [];

    public event Action<AppWindow?>? Completed;

    public void Load()
    {
        _allWindows = [
            new AppWindow(IntPtr.Zero, "Entire Screen", ""),
            .. ScreenStreamService.GetOpenWindows()
        ];
        FilteredWindows = _allWindows;
        SelectedWindow = _allWindows.FirstOrDefault();
    }

    // Stream Game: likely games first (🎮), then every other window in case detection missed it.
    // No "Entire Screen" — Stream Game always captures a single window.
    public void LoadForGame(GameDetectionResult detection, IReadOnlyList<GameWindowFacts> all)
    {
        Heading = "Choose your game";
        Hint = "Pick the game to stream";
        var likely = detection.Ranked
            .Select(c => new AppWindow((IntPtr)c.Window.Handle, c.Window.Title, c.Window.ProcessName, c.Window.ProcessId, true, c.GameName))
            .ToList();
        var likelyHandles = likely.Select(w => w.Handle).ToHashSet();
        var others = all
            .Where(f => !likelyHandles.Contains((IntPtr)f.Handle) && f.ProcessId != Environment.ProcessId)
            .OrderBy(f => f.ProcessName).ThenBy(f => f.Title)
            .Select(f => new AppWindow((IntPtr)f.Handle, f.Title, f.ProcessName, f.ProcessId));
        _allWindows = [.. likely, .. others];
        FilteredWindows = _allWindows;
        SelectedWindow = _allWindows.FirstOrDefault();
    }

    partial void OnSearchTextChanged(string value)
    {
        FilteredWindows = string.IsNullOrWhiteSpace(value)
            ? _allWindows
            : _allWindows
                .Where(w => w.Title.Contains(value, StringComparison.OrdinalIgnoreCase)
                         || w.ProcessName.Contains(value, StringComparison.OrdinalIgnoreCase))
                .ToList();
    }

    [RelayCommand]
    public void Share() => Completed?.Invoke(SelectedWindow);

    [RelayCommand]
    public void Cancel() => Completed?.Invoke(null);
}
