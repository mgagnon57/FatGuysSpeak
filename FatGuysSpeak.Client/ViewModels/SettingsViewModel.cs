using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FatGuysSpeak.Client.Services;

namespace FatGuysSpeak.Client.ViewModels;

public partial class SettingsViewModel(ApiService api, AudioService audio, PttService ptt) : ObservableObject
{
    [ObservableProperty] private List<AudioDevice> _inputDevices = [];
    [ObservableProperty] private List<AudioDevice> _outputDevices = [];
    [ObservableProperty] private AudioDevice? _selectedInputDevice;
    [ObservableProperty] private AudioDevice? _selectedOutputDevice;
    [ObservableProperty] private double _inputGain = 1.0;
    [ObservableProperty] private double _outputVolume = 1.0;
    [ObservableProperty] private string _pttKeyName = "(not set)";
    [ObservableProperty] private bool _isSettingPttKey;
    [ObservableProperty] private bool _adaptiveStreamQuality = true;
    [ObservableProperty] private string _serverUrl = "";

    public string Username => api.CurrentUsername;
    public string InputGainPercent => $"{(int)(InputGain * 100)}%";
    public string OutputVolumePercent => $"{(int)(OutputVolume * 100)}%";
    public string PttButtonLabel => IsSettingPttKey ? "Press any key…" : "Rebind";
    public Color PttButtonColor => IsSettingPttKey ? Color.FromArgb("#f0a030") : Color.FromArgb("#4e5058");
    public bool IsPttNotSet => PttKeyName == "(not set)";

    public void Load()
    {
        OnPropertyChanged(nameof(Username));
        InputDevices = AudioService.GetInputDevices();
        OutputDevices = AudioService.GetOutputDevices();
        SelectedInputDevice = InputDevices.FirstOrDefault(d => d.Index == audio.InputDeviceIndex) ?? InputDevices.FirstOrDefault();
        SelectedOutputDevice = OutputDevices.FirstOrDefault(d => d.Index == audio.OutputDeviceIndex) ?? OutputDevices.FirstOrDefault();
        InputGain = audio.InputGain;
        OutputVolume = audio.OutputVolume;
        PttKeyName = ptt.PttKeyName;
        AdaptiveStreamQuality = Preferences.Get("adaptive_quality", true);
        ServerUrl = api.ServerUrl;
        ptt.KeyLearned += OnKeyLearned;
    }

    public void Unload()
    {
        ptt.KeyLearned -= OnKeyLearned;
        ptt.CancelLearning();
        IsSettingPttKey = false;
    }

    private void OnKeyLearned(string name)
    {
        PttKeyName = name;
        IsSettingPttKey = false;
        OnPropertyChanged(nameof(IsPttNotSet));
    }

    partial void OnSelectedInputDeviceChanged(AudioDevice? value)
    {
        if (value is null) return;
        audio.InputDeviceIndex = value.Index;
        Preferences.Set("audio_input_device", value.Index);
    }

    partial void OnSelectedOutputDeviceChanged(AudioDevice? value)
    {
        if (value is null) return;
        audio.OutputDeviceIndex = value.Index;
        Preferences.Set("audio_output_device", value.Index);
    }

    partial void OnInputGainChanged(double value)
    {
        audio.InputGain = (float)value;
        Preferences.Set("audio_input_gain", (float)value);
        OnPropertyChanged(nameof(InputGainPercent));
    }

    partial void OnOutputVolumeChanged(double value)
    {
        audio.OutputVolume = (float)value;
        Preferences.Set("audio_output_volume", (float)value);
        OnPropertyChanged(nameof(OutputVolumePercent));
    }

    partial void OnAdaptiveStreamQualityChanged(bool value) =>
        Preferences.Set("adaptive_quality", value);

    partial void OnServerUrlChanged(string value) =>
        Preferences.Set("server_url", value.TrimEnd('/'));

    partial void OnIsSettingPttKeyChanged(bool value)
    {
        OnPropertyChanged(nameof(PttButtonLabel));
        OnPropertyChanged(nameof(PttButtonColor));
    }

    [RelayCommand]
    public void RebindPttKey()
    {
        if (!ptt.IsHookInstalled || IsSettingPttKey) return;
        IsSettingPttKey = true;
        ptt.BeginLearning();
    }
}
