using System.Reflection;
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
    [ObservableProperty] private bool _isNoiseSuppressionEnabled;
    [ObservableProperty] private double _noiseGateThreshold = 0.05;
    [ObservableProperty] private bool _isAdaptiveThresholdEnabled;
    [ObservableProperty] private string _selectedTheme = ThemeService.Dark;

    // Opt-in privacy. Server-enforced; the toggle just calls the API. Off by default.
    [ObservableProperty] private bool _privateMode;
    [ObservableProperty] private bool _isPrivateModeBusy;
    private bool _suppressPrivateModeSave;

    // The Settings page runs in its own Window (not the Shell), so Shell.Current is null here —
    // dialogs must go through the page itself. Set by MainViewModel.OpenSettings.
    public Page? HostPage { get; set; }
    private Page? Dialog => HostPage ?? Application.Current?.Windows?.LastOrDefault()?.Page;

    partial void OnPrivateModeChanged(bool value)
    {
        if (_suppressPrivateModeSave) return;
        _ = OnSwitchToggledAsync(value);
    }

    // Drives the Settings switch. The actual consent/apply lives in ConfirmAndApplyPrivateModeAsync
    // so the channel-toolbar dropdown can reuse it; on a declined/failed change we snap the switch back.
    private async Task OnSwitchToggledAsync(bool value)
    {
        IsPrivateModeBusy = true;
        try
        {
            var ok = await ConfirmAndApplyPrivateModeAsync(value, Dialog);
            if (!ok) SetPrivateModeSilently(!value);
        }
        finally { IsPrivateModeBusy = false; }
    }

    // Update the bound PrivateMode flag without re-triggering the consent/apply flow.
    public void SetPrivateModeSilently(bool value)
    {
        _suppressPrivateModeSave = true;
        PrivateMode = value;
        _suppressPrivateModeSave = false;
    }

    /// <summary>
    /// Shared consent + apply for Private Mode, used by both the Settings switch and the channel
    /// dropdown. Turning OFF prompts for consent (voice will be used by AI); turning ON shows the
    /// data-deletion notice. Returns true only if the change was accepted and persisted server-side.
    /// Does NOT mutate the bound PrivateMode flag — the caller updates UI state on success.
    /// </summary>
    public async Task<bool> ConfirmAndApplyPrivateModeAsync(bool value, Page? host)
    {
        host ??= Dialog;
        if (!value)
        {
            if (host is null) return false;   // can't get consent without a UI
            var accepted = await host.DisplayAlert(
                "🎙️ Let PorkChop Listen In?",
                "Flip this off and PorkChop gets your voice. Every word you say in voice chat gets " +
                "transcribed, stored on the server, and used to roast you, profile you, slap dumb " +
                "nicknames on you, and drag you into the recaps. He's gonna have a field day.\n\n" +
                "You cool with that, you magnificent degenerate?",
                "🔥 Hell yeah, roast me", "🔒 Nope, stay private");
            if (!accepted) return false;
        }

        var ok = await api.SetPrivateModeAsync(value);
        if (!ok) return false;

        Preferences.Set("private_mode", value);   // client-side hint (e.g. to suppress local STT posting)
        if (value && host is not null)
            await host.DisplayAlert(
                "🔒 Private Mode On — PorkChop's Pissed",
                "Done. PorkChop just had everything he scraped on you yanked off the server — your " +
                "voice transcripts AND the dumb nicknames he cooked up, gone, even the old stuff he'd " +
                "been hoarding. All that's left is what runs your account: your username, password, and " +
                "the messages you actually typed.\n\n" +
                "From here on he can't hear you, roast you, or recap you. Enjoy the silence. 🐷🔇",
                "😎 Sweet");
        return true;
    }

    private async Task LoadPrivateModeAsync()
    {
        var pm = await api.GetPrivateModeAsync();
        _suppressPrivateModeSave = true;
        PrivateMode = pm;
        _suppressPrivateModeSave = false;
        Preferences.Set("private_mode", pm);
    }

    // All available themes, in display order — drives the Settings picker.
    public IReadOnlyList<string> ThemeNames => ThemeService.Names;

    public string Username => api.CurrentUsername;
    public string AppVersion
    {
        get
        {
            var info = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var v = FatGuysSpeak.Shared.VersionInfo.Parse(info);
            return v.Commit.Length > 0 ? $"{v.Version} ({v.Commit})" : v.Version;
        }
    }
    public string InputGainPercent => $"{(int)(InputGain * 100)}%";
    public string OutputVolumePercent => $"{(int)(OutputVolume * 100)}%";
    public string NoiseGateThresholdPercent => $"{(int)(NoiseGateThreshold * 100)}%";
    public bool IsThresholdSliderEnabled => IsNoiseSuppressionEnabled && !IsAdaptiveThresholdEnabled;
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
        IsNoiseSuppressionEnabled = audio.NoiseGateEnabled;
        NoiseGateThreshold = audio.NoiseGateThreshold;
        IsAdaptiveThresholdEnabled = audio.AdaptiveThresholdEnabled;
        SelectedTheme = ThemeService.CurrentTheme;
        _ = LoadPrivateModeAsync();
        audio.ThresholdChanged += OnThresholdChanged;
        ptt.KeyLearned += OnKeyLearned;
    }

    public void Unload()
    {
        audio.ThresholdChanged -= OnThresholdChanged;
        ptt.KeyLearned -= OnKeyLearned;
        ptt.CancelLearning();
        IsSettingPttKey = false;
    }

    private void OnThresholdChanged(double value) =>
        MainThread.BeginInvokeOnMainThread(() => NoiseGateThreshold = value);

    partial void OnPttKeyNameChanged(string value) => OnPropertyChanged(nameof(IsPttNotSet));

    private void OnKeyLearned(string name)
    {
        PttKeyName = name;
        IsSettingPttKey = false;
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

    partial void OnIsNoiseSuppressionEnabledChanged(bool value)
    {
        audio.NoiseGateEnabled = value;
        Preferences.Set("noise_gate_enabled", value);
        OnPropertyChanged(nameof(IsThresholdSliderEnabled));
    }

    partial void OnNoiseGateThresholdChanged(double value)
    {
        audio.NoiseGateThreshold = value;
        if (!IsAdaptiveThresholdEnabled)
            Preferences.Set("noise_gate_threshold", (float)value);
        OnPropertyChanged(nameof(NoiseGateThresholdPercent));
    }

    partial void OnIsAdaptiveThresholdEnabledChanged(bool value)
    {
        audio.AdaptiveThresholdEnabled = value;
        Preferences.Set("adaptive_threshold_enabled", value);
        OnPropertyChanged(nameof(IsThresholdSliderEnabled));
    }

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

    [RelayCommand]
    public void SetTheme(string themeName)
    {
        ThemeService.Apply(themeName);
        SelectedTheme = themeName;
    }
}
