using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FatGuysSpeak.Client.Models;
using FatGuysSpeak.Client.Services;

namespace FatGuysSpeak.Client.ViewModels;

public partial class WindowPickerViewModel : ObservableObject
{
    [ObservableProperty] private List<AppWindow> _filteredWindows = [];
    [ObservableProperty] private AppWindow? _selectedWindow;
    [ObservableProperty] private string _searchText = "";

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
