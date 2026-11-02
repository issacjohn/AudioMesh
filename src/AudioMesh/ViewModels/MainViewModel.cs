using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using AudioMesh.Audio;
using AudioMesh.Services;
using NAudio.CoreAudioApi;
using Brush = System.Windows.Media.Brush;

namespace AudioMesh.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly Brush CalibrationOkBrush = Freeze("#35C58A");
    private static readonly Brush CalibrationWarnBrush = Freeze("#F2A33A");
    private static readonly Brush CalibrationErrorBrush = Freeze("#FF5F57");
    private static readonly Brush CalibrationActiveBrush = Freeze("#4C8DFF");
    private static readonly Brush CalibrationIdleBrush = Freeze("#8A93A6");

    private static Brush Freeze(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    private readonly AudioEngine _engine = new();
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _scanTimer;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    private bool _isRunning;
    private bool _perfectSync;
    private bool _monoOutput;
    private bool _orderApplied;
    private bool _isCalibrating;
    private bool _isRefreshing;
    private bool _isRescanning;
    private bool _autoStart;
    private bool _launchAtLogin;
    private LatencyMode _latencyMode;
    private LatencyOption _selectedLatencyOption = LatencyOption.For(LatencyMode.Stable);
    private bool _isEqOpen;
    private bool _isAboutOpen;
    private DeviceViewModel? _eqDevice;
    private MicrophoneOption? _selectedMicrophone;
    private string _calibrationStatus = "Uses the selected microphone to align the enabled speakers.";
    private string? _savedDefaultId;
    private string _statusText = "Idle";
    private bool _disposed;

    public MainViewModel()
    {
        _settings = SettingsStore.Load();
        _perfectSync = _settings.PerfectSync;
        _monoOutput = _settings.MonoOutput;
        _autoStart = _settings.AutoStart;
        _launchAtLogin = _settings.LaunchAtLogin;
        StartupRegistration.SetEnabled(_launchAtLogin);
        _latencyMode = ParseLatencyMode(_settings);
        _selectedLatencyOption = LatencyOption.For(_latencyMode);
        AudioLatency.Mode = _latencyMode;
        _engine.MonoOutput = _monoOutput;

        if (Enum.TryParse(_settings.Source, out SourceKind savedSource))
            _engine.SetSource(savedSource);

        LoadMicrophonesAsync();

        ToggleCommand = new RelayCommand(_ => ToggleRouting());
        RefreshCommand = new RelayCommand(_ => RefreshAll());
        CalibrateCommand = new RelayCommand(_ => RunCalibration());
        CloseEqCommand = new RelayCommand(_ => CloseEq());
        OpenAboutCommand = new RelayCommand(_ => OpenAbout());
        CloseAboutCommand = new RelayCommand(_ => IsAboutOpen = false);
        CloseOverlayCommand = new RelayCommand(_ => CloseOverlay());
        OpenSupportCommand = new RelayCommand(_ => ExternalLink.Open("https://buymeacoffee.com/issac_john"));
        OpenGitHubCommand = new RelayCommand(_ => ExternalLink.Open("https://github.com/issacjohn/AudioMesh"));

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _uiTimer.Tick += (_, _) => Tick();
        _uiTimer.Start();

        _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _scanTimer.Tick += async (_, _) =>
        {
            // Switch the capture first: if the default device just became routable,
            // enabling it before the capture moves would briefly feed our own output back.
            _engine.EnsureSource();
            await RescanAsync();
        };
        _scanTimer.Start();

        RescanSync();
        ApplyPerfectSync();
        UpdateStatus();

        if (_autoStart && Devices.Any(d => d.IsEnabled) && !IsRunning)
            ToggleRouting();
    }

    public ObservableCollection<DeviceViewModel> Devices { get; } = new();
    public ObservableCollection<MicrophoneOption> Microphones { get; } = new();

    public RelayCommand ToggleCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand CalibrateCommand { get; }
    public RelayCommand CloseEqCommand { get; }
    public RelayCommand OpenAboutCommand { get; }
    public RelayCommand CloseAboutCommand { get; }
    public RelayCommand CloseOverlayCommand { get; }
    public RelayCommand OpenSupportCommand { get; }
    public RelayCommand OpenGitHubCommand { get; }

    public DeviceViewModel? EqDevice
    {
        get => _eqDevice;
        private set => Set(ref _eqDevice, value);
    }

    public (double Left, double Top, double Width, double Height, bool Maximized) GetWindowPlacement() =>
        (_settings.WindowLeft, _settings.WindowTop, _settings.WindowWidth, _settings.WindowHeight,
         _settings.WindowMaximized);

    public void SetWindowPlacement(double left, double top, double width, double height, bool maximized)
    {
        _settings.WindowLeft = left;
        _settings.WindowTop = top;
        _settings.WindowWidth = width;
        _settings.WindowHeight = height;
        _settings.WindowMaximized = maximized;
    }

    public bool IsEqOpen
    {
        get => _isEqOpen;
        private set => Set(ref _isEqOpen, value);
    }

    public bool IsAboutOpen
    {
        get => _isAboutOpen;
        private set => Set(ref _isAboutOpen, value);
    }

    public string VersionText
    {
        get
        {
            Version? version = typeof(MainViewModel).Assembly.GetName().Version;
            return version == null ? "Version 1.0.0" : $"Version {version.Major}.{version.Minor}.{version.Build}";
        }
    }

    /// <summary>
    /// When enabled, the app points the Windows default output at an unused endpoint
    /// and captures that. Every real speaker is then a normal routed endpoint, so all
    /// of them share one pipeline and stay sample-accurate with each other.
    /// The previous default is restored when the toggle is turned off or the app exits.
    /// </summary>
    public bool PerfectSync
    {
        get => _perfectSync;
        set
        {
            if (!Set(ref _perfectSync, value)) return;

            if (value) ApplyPerfectSync();
            else RestoreSystemDefault();

            OnPropertyChanged(nameof(HintText));
            UpdateStatus();
        }
    }

    public bool HasDevices => Devices.Count > 0;

    /// <summary>
    /// One Bluetooth radio realistically carries one or two A2DP links. Warn when the
    /// user enables three or more, because the third link usually degrades or drops.
    /// </summary>
    public bool ShowBluetoothWarning => BluetoothSpeakerCount >= 3;

    public int BluetoothSpeakerCount => Devices.Count(d => d.IsEnabled && d.IsBluetooth);

    public string BluetoothWarningText =>
        $"{BluetoothSpeakerCount} Bluetooth speakers are enabled. One Bluetooth radio usually " +
        "cannot carry three audio streams reliably — connect one speaker by wire or use a " +
        "second Bluetooth adapter.";

    /// <summary>Starts routing as soon as the app opens.</summary>
    public bool AutoStart
    {
        get => _autoStart;
        set
        {
            if (!Set(ref _autoStart, value)) return;
            Save();
        }
    }

    /// <summary>Launches the app at sign-in and keeps it in the tray.</summary>
    public bool LaunchAtLogin
    {
        get => _launchAtLogin;
        set
        {
            if (!Set(ref _launchAtLogin, value)) return;
            StartupRegistration.SetEnabled(value);
            Save();
        }
    }

    public MicrophoneOption? SelectedMicrophone
    {
        get => _selectedMicrophone;
        set
        {
            if (!Set(ref _selectedMicrophone, value)) return;
            _settings.CalibrationMicId = value?.Id ?? string.Empty;
        }
    }

    /// <summary>
    /// Buffer trade-off. <see cref="LatencyMode.Latency"/> gets closest to direct playback;
    /// <see cref="LatencyMode.Stable"/> keeps the most headroom for jittery Bluetooth links.
    /// </summary>
    public IReadOnlyList<LatencyOption> LatencyOptions => LatencyOption.All;

    public LatencyOption SelectedLatencyOption
    {
        get => _selectedLatencyOption;
        set
        {
            if (value == null || !Set(ref _selectedLatencyOption, value)) return;
            if (_latencyMode == value.Mode) return;

            _latencyMode = value.Mode;
            AudioLatency.Mode = value.Mode;
            OnPropertyChanged(nameof(LatencyHint));
            OnPropertyChanged(nameof(LatencyEstimate));
            RebuildForLatency();
            Save();
        }
    }

    public string LatencyHint => _selectedLatencyOption.Hint;

    public string LatencyEstimate => _selectedLatencyOption.Estimate;

    /// <summary>
    /// Downmixes the captured stream to mono before it is fanned out, so every endpoint
    /// hears the exact same signal. Useful for a room full of speakers where stereo
    /// imaging does not matter. Persisted across launches.
    /// </summary>
    public bool MonoOutput
    {
        get => _monoOutput;
        set
        {
            if (!Set(ref _monoOutput, value)) return;
            _engine.MonoOutput = value;

            foreach (DeviceViewModel device in Devices)
                device.RefreshSpatialState();

            OnPropertyChanged(nameof(IsStereoOutput));
            OnPropertyChanged(nameof(HintText));
        }
    }

    /// <summary>Mirror of <see cref="MonoOutput"/> for the segmented control.</summary>
    public bool IsStereoOutput
    {
        get => !_monoOutput;
        set
        {
            if (value) MonoOutput = false;
        }
    }

    /// <summary>True while the microphone calibration routine is running.</summary>
    public bool IsCalibrating
    {
        get => _isCalibrating;
        private set
        {
            if (!Set(ref _isCalibrating, value)) return;
            OnPropertyChanged(nameof(CanCalibrate));
            OnPropertyChanged(nameof(CalibrateButtonText));
        }
    }

    public bool CanCalibrate => !_isCalibrating;

    public string CalibrateButtonText => _isCalibrating ? "Measuring…" : "Calibrate";

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (!Set(ref _isRefreshing, value)) return;
            OnPropertyChanged(nameof(CanRefresh));
        }
    }

    public bool CanRefresh => !_isRefreshing;

    public string CalibrationStatus
    {
        get => _calibrationStatus;
        private set
        {
            if (!Set(ref _calibrationStatus, value)) return;
            OnPropertyChanged(nameof(CalibrationStatusBrush));
        }
    }

    public Brush CalibrationStatusBrush
    {
        get
        {
            if (_calibrationStatus.StartsWith("Aligned", StringComparison.Ordinal)) return CalibrationOkBrush;
            if (_calibrationStatus.StartsWith("No speaker", StringComparison.Ordinal) ||
                _calibrationStatus.StartsWith("Calibration failed", StringComparison.Ordinal))
                return CalibrationErrorBrush;
            if (_calibrationStatus.StartsWith("Keep", StringComparison.Ordinal) ||
                _calibrationStatus.StartsWith("Measuring", StringComparison.Ordinal))
                return CalibrationActiveBrush;
            if (_calibrationStatus.StartsWith("Measured", StringComparison.Ordinal) ||
                _calibrationStatus.StartsWith("Enable", StringComparison.Ordinal))
                return CalibrationWarnBrush;
            return CalibrationIdleBrush;
        }
    }

    public string SummaryText
    {
        get
        {
            int routed = Devices.Count(d => d.IsEnabled);
            return Devices.Count == 0
                ? "No active playback endpoints detected."
                : $"{Devices.Count} endpoint{(Devices.Count == 1 ? "" : "s")} · {routed} routed";
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!Set(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(StartButtonText));
        }
    }

    public string StartButtonText => IsRunning ? "Stop" : "Start routing";

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string HintText
    {
        get
        {
            string channel = _monoOutput
                ? "Mono is on: every speaker plays the same L+R mix."
                : "Tip: use Left only / Right only for a stereo pair, or Center / Rear / Subwoofer to build surround.";

            if (_engine.Source == SourceKind.SystemAudio && _perfectSync)
                return "Perfect sync is on: Windows' default output is a silent endpoint, so every speaker is routed " +
                       "through the same pipeline and stays in sync. Your normal default is restored when you turn this off or close the app. " +
                       channel;

            if (_engine.Source == SourceKind.SystemAudio && Devices.Any(d => d.IsDefault))
                return "The default device plays natively, so routed speakers trail it by the pipeline latency. " +
                       "Turn on Perfect sync to capture from a silent output instead and route every speaker identically. " +
                       channel;

            return "Bluetooth endpoints usually need 150–250 ms of sync delay to line up with wired ones. " + channel;
        }
    }

    public bool IsSystemSource
    {
        get => _engine.Source == SourceKind.SystemAudio;
        set
        {
            if (!value) return;
            _engine.SetSource(SourceKind.SystemAudio);
            RefreshRouteState();
            if (_perfectSync) ApplyPerfectSync();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsToneSource));
            UpdateStatus();
        }
    }

    public bool IsToneSource
    {
        get => _engine.Source == SourceKind.TestTone;
        set
        {
            if (!value) return;
            _engine.SetSource(SourceKind.TestTone);
            // No capture in tone mode, so hand the system default back to the user.
            if (_perfectSync) RestoreSystemDefault();
            RefreshRouteState();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSystemSource));
            UpdateStatus();
        }
    }

    private void RefreshRouteState()
    {
        foreach (DeviceViewModel device in Devices)
            device.OnSourceChanged();

        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HintText));
    }

    private void ApplyPerfectSync()
    {
        if (!_perfectSync || _disposed) return;
        if (_engine.Source != SourceKind.SystemAudio) return;

        DeviceViewModel? silent = PickSilentCapture();
        if (silent == null)
        {
            StatusText = "Perfect sync needs an unused output to capture from — connect or enable one more device.";
            return;
        }

        string? current = CurrentDefaultId();
        if (current != null && !string.Equals(current, silent.Id, StringComparison.Ordinal))
            _savedDefaultId ??= current;

        if (!AudioDefaults.SetDefault(silent.Id))
        {
            StatusText = "Could not change the Windows default output.";
            return;
        }

        ScheduleRefresh();
    }

    private void RestoreSystemDefault()
    {
        if (_savedDefaultId == null) return;

        AudioDefaults.SetDefault(_savedDefaultId);
        _savedDefaultId = null;
        ScheduleRefresh();
    }

    /// <summary>An output we are not driving: ideal as the silent capture source.</summary>
    private DeviceViewModel? PickSilentCapture()
    {
        var candidates = Devices.Where(d => !d.IsDesiredEnabled).ToList();
        if (candidates.Count == 0) return null;

        return candidates.FirstOrDefault(d => !d.IsBluetooth &&
                                              d.Name.Contains("Digital", StringComparison.OrdinalIgnoreCase))
               ?? candidates.FirstOrDefault(d => !d.IsBluetooth)
               ?? candidates[0];
    }

    private string? CurrentDefaultId()
    {
        try
        {
            using MMDevice device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.ID;
        }
        catch
        {
            return null;
        }
    }

    private void ScheduleRefresh()
    {
        if (_disposed) return;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            _engine.EnsureSource();
            await RescanAsync();
            UpdateStatus();
        };
        timer.Start();
    }

    private async void LoadMicrophonesAsync()
    {
        try
        {
            (List<MicrophoneOption> list, string? defaultId) = await Task.Run(EnumerateMicrophones);
            ApplyMicrophones(list, defaultId);
        }
        catch
        {
            // the list can be refreshed again from the header
        }
    }

    /// <summary>Runs off the UI thread: only plain strings leave this method.</summary>
    private static (List<MicrophoneOption> List, string? DefaultId) EnumerateMicrophones()
    {
        var list = new List<MicrophoneOption>();
        string? defaultId = null;

        using var enumerator = new MMDeviceEnumerator();

        try
        {
            using MMDevice def = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            defaultId = def.ID;
        }
        catch
        {
            // no default microphone right now
        }

        try
        {
            var captures = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            for (int i = 0; i < captures.Count; i++)
            {
                MMDevice device = captures[i];
                list.Add(new MicrophoneOption(device.ID, device.FriendlyName));
                device.Dispose();
            }
        }
        catch
        {
            // enumeration failure just leaves the list empty
        }

        return (list, defaultId);
    }

    private void ApplyMicrophones(List<MicrophoneOption> list, string? defaultId)
    {
        string keep = _selectedMicrophone?.Id ?? _settings.CalibrationMicId;

        Microphones.Clear();
        foreach (MicrophoneOption microphone in list)
            Microphones.Add(microphone);

        _selectedMicrophone = Microphones.FirstOrDefault(m => m.Id == keep)
                           ?? Microphones.FirstOrDefault(m => m.Id == defaultId)
                           ?? Microphones.FirstOrDefault();

        _settings.CalibrationMicId = _selectedMicrophone?.Id ?? string.Empty;
        OnPropertyChanged(nameof(SelectedMicrophone));
    }

    /// <summary>
    /// Plays a short burst on each enabled speaker and measures the arrival with the
    /// selected microphone, then writes the relative delays into the cards.
    /// </summary>
    private async void RunCalibration()
    {
        if (_isCalibrating) return;

        List<DeviceViewModel> targets = Devices.Where(d => d.IsEnabled).ToList();
        if (targets.Count < 2)
        {
            CalibrationStatus = "Enable at least two speakers before calibrating.";
            return;
        }

        bool wasRunning = IsRunning;
        var savedMutes = targets.ToDictionary(d => d.Id, d => d.Muted, StringComparer.Ordinal);

        _scanTimer.Stop();
        IsCalibrating = true;
        CalibrationStatus = "Keep the room quiet — measuring…";

        try
        {
            // Keep every link warm even when the user has not pressed Start routing:
            // a cold A2DP link can take seconds to open, so the first probe would miss.
            if (!wasRunning)
            {
                _engine.Start();
                IsRunning = true;
            }

            foreach (DeviceViewModel device in targets)
                device.Muted = true;

            await Task.Delay(1500);

            var calibrationTargets = targets
                .Select(d => new CalibrationTarget(d.Id, d.Name, d.IsBluetooth))
                .ToList();

            IReadOnlyList<CalibrationResult> results = await Task.Run(() =>
                new SpeakerCalibrator().Measure(
                    calibrationTargets,
                    SelectedMicrophone?.Id,
                    message => _dispatcher.BeginInvoke(new Action(() => CalibrationStatus = message))));

            foreach (CalibrationResult result in results)
            {
                if (result.DelayMs < 0) continue;

                DeviceViewModel? device = Devices.FirstOrDefault(d => d.Id == result.Id);
                if (device != null) device.DelayMs = result.DelayMs;
            }

            int measured = results.Count(r => r.DelayMs >= 0);
            bool capped = results.Any(r => r.DelayMs >= 500);
            bool allHeard = measured == results.Count;
            string summary = string.Join(" · ", results.Select(r =>
            {
                if (r.DelayMs >= 0) return $"{ShortName(r.Name)} {r.DelayMs} ms";
                return r.EndpointPeak > 0.01f
                    ? $"{ShortName(r.Name)} not heard (signal sent)"
                    : $"{ShortName(r.Name)} no output";
            }));

            if (measured == 0)
                CalibrationStatus = "No speaker reached the microphone: " + summary +
                                    " — check the mic and the speaker links.";
            else if (allHeard && !capped)
                CalibrationStatus = "Aligned: " + summary;
            else
                CalibrationStatus = $"Measured {measured} of {results.Count}: " + summary +
                                    (capped
                                        ? " — a link looks unstable; try again."
                                        : " — a speaker did not reach the microphone. Check TWS, wire it, or move the mic closer.");
        }
        catch (Exception ex)
        {
            CalibrationStatus = "Calibration failed: " + ex.Message;
        }
        finally
        {
            foreach (DeviceViewModel device in targets)
                if (savedMutes.TryGetValue(device.Id, out bool muted))
                    device.Muted = muted;

            if (!wasRunning)
            {
                _engine.Stop();
                IsRunning = false;
            }

            Save();
            IsCalibrating = false;
            _scanTimer.Start();
            UpdateStatus();
        }
    }

    private void OpenEq(DeviceViewModel device)
    {
        IsAboutOpen = false;
        EqDevice = device;
        IsEqOpen = true;
    }

    private void OpenAbout()
    {
        if (_isEqOpen)
        {
            IsEqOpen = false;
            EqDevice = null;
        }

        IsAboutOpen = true;
    }

    private void CloseOverlay()
    {
        if (_isAboutOpen)
        {
            IsAboutOpen = false;
            return;
        }

        CloseEq();
    }

    private void CloseEq()
    {
        if (!_isEqOpen) return;
        IsEqOpen = false;
        EqDevice = null;
        Save();
    }

    private static LatencyMode ParseLatencyMode(AppSettings settings)
    {
        if (Enum.TryParse(settings.LatencyMode, true, out LatencyMode mode) &&
            Enum.IsDefined(typeof(LatencyMode), mode))
        {
            return mode;
        }

        // Migrate the old on/off low-latency setting.
        return settings.LowLatency ? LatencyMode.Latency : LatencyMode.Stable;
    }

    /// <summary>Rebuilds the channels so the new buffer sizes take effect immediately.</summary>
    private void RebuildForLatency()
    {
        bool wasRunning = IsRunning;

        try
        {
            if (wasRunning)
            {
                _engine.Stop();
                IsRunning = false;
            }

            foreach (DeviceViewModel device in Devices.Where(d => d.IsEnabled).ToList())
            {
                device.IsEnabled = false;
                device.IsEnabled = true;
            }

            if (wasRunning)
            {
                _engine.Start();
                IsRunning = true;
            }
        }
        catch (Exception ex)
        {
            StatusText = "Could not apply the latency setting: " + ex.Message;
        }

        UpdateStatus();
    }

    private static string ShortName(string name)
    {
        int open = name.IndexOf('(');
        int close = name.LastIndexOf(')');
        if (open >= 0 && close > open) return name[(open + 1)..close].Trim();
        return name;
    }

    private void ToggleRouting()
    {
        if (IsRunning)
        {
            _engine.Stop();
            IsRunning = false;
        }
        else
        {
            try
            {
                _engine.Start();
                IsRunning = true;
            }
            catch (Exception ex)
            {
                StatusText = "Could not start: " + ex.Message;
                return;
            }
        }

        UpdateStatus();
    }

    private void Tick()
    {
        foreach (DeviceViewModel device in Devices)
            device.Refresh();

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (!IsRunning)
        {
            StatusText = "Idle — enable one or more endpoints, then start routing.";
            return;
        }

        int routed = Devices.Count(d => d.IsEnabled);
        int active = Devices.Count(d => d.IsEnabled && d.IsLocked);

        string source = _engine.Source == SourceKind.SystemAudio ? "system audio" : "test tone";
        StatusText = $"Routing {source} to {routed} endpoint{(routed == 1 ? "" : "s")}" +
                     (active > 0 ? $" · {active} locked" : " · acquiring clocks…");

        if (_engine.LastError != null)
            StatusText += " · " + _engine.LastError;
    }

    /// <summary>Refreshes both the speaker list and the calibration microphone list.</summary>
    private async void RefreshAll()
    {
        if (_isRefreshing) return;

        IsRefreshing = true;
        try
        {
            await Task.Delay(200);
            await Dispatcher.Yield(DispatcherPriority.Render);

            await RescanAsync();

            (List<MicrophoneOption> list, string? defaultId) = await Task.Run(EnumerateMicrophones);
            ApplyMicrophones(list, defaultId);

            UpdateStatus();
        }
        catch (Exception ex)
        {
            StatusText = "Refresh failed: " + ex.Message;
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>Initial synchronous scan so the first layout is ready before the window shows.</summary>
    private void RescanSync()
    {
        List<EndpointSnapshot> found;
        try
        {
            found = EndpointCatalog.Snapshot(_enumerator);
        }
        catch (Exception ex)
        {
            StatusText = "Audio subsystem error: " + ex.Message;
            return;
        }

        ApplySnapshots(found);
    }

    /// <summary>Enumerates on a worker thread; only plain records touch the UI.</summary>
    private async Task RescanAsync()
    {
        if (_isRescanning) return;
        _isRescanning = true;

        try
        {
            List<EndpointSnapshot> found;
            try
            {
                found = await Task.Run(() =>
                {
                    using var enumerator = new MMDeviceEnumerator();
                    return EndpointCatalog.Snapshot(enumerator);
                });
            }
            catch (Exception ex)
            {
                StatusText = "Audio subsystem error: " + ex.Message;
                return;
            }

            try
            {
                ApplySnapshots(found);
            }
            catch (Exception ex)
            {
                StatusText = "Could not update the device list: " + ex.Message;
            }
        }
        finally
        {
            _isRescanning = false;
        }
    }

    private void ApplySnapshots(List<EndpointSnapshot> found)
    {
        var liveIds = found.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);

        for (int i = Devices.Count - 1; i >= 0; i--)
        {
            if (liveIds.Contains(Devices[i].Id)) continue;
            if (ReferenceEquals(EqDevice, Devices[i]))
            {
                IsEqOpen = false;
                EqDevice = null;
            }
            Devices[i].PropertyChanged -= OnDeviceChanged;
            Devices[i].Dispose();
            Devices.RemoveAt(i);
        }

        var added = new List<DeviceViewModel>();

        foreach (EndpointSnapshot info in found)
        {
            DeviceViewModel? existing = Devices.FirstOrDefault(d => d.Id == info.Id);
            if (existing != null)
            {
                existing.UpdateMeta(info);
                continue;
            }

            try
            {
                MMDevice device = _enumerator.GetDevice(info.Id);
                var endpoint = new EndpointInfo
                {
                    Device = device,
                    Name = info.Name,
                    Kind = info.Kind,
                    IsBluetooth = info.IsBluetooth,
                    IsDefault = info.IsDefault,
                    Rate = info.Rate,
                    Channels = info.Channels
                };

                _settings.Devices.TryGetValue(info.Id, out DeviceSettings? saved);
                var vm = new DeviceViewModel(endpoint, _engine, saved, MoveDevice, OpenEq);
                vm.PropertyChanged += OnDeviceChanged;
                Devices.Add(vm);
                added.Add(vm);
            }
            catch
            {
                // the endpoint disappeared between snapshot and apply
            }
        }

        if (!_orderApplied)
        {
            // The first scan with devices restores the arrangement the user left behind.
            // An empty scan (audio service still starting) must not consume this step.
            if (found.Count > 0)
            {
                ApplySavedOrder();
                _orderApplied = true;
            }
        }
        else if (added.Count > 0)
        {
            // A device that shows up later (a speaker reconnecting, a plugged-in DAC)
            // starts at the top so it is not buried under the existing cards.
            DeviceOrdering.PromoteToTop(Devices, added);
            Save();
        }

        RefreshMoveState();

        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HintText));
        OnPropertyChanged(nameof(ShowBluetoothWarning));
        OnPropertyChanged(nameof(BluetoothWarningText));
    }

    private void MoveDevice(DeviceViewModel device, int delta)
    {
        int index = Devices.IndexOf(device);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= Devices.Count) return;

        Devices.Move(index, target);
        RefreshMoveState();

        // Persist immediately: a crash or force-close should not lose the arrangement.
        Save();
    }

    private void RefreshMoveState()
    {
        for (int i = 0; i < Devices.Count; i++)
            Devices[i].SetMoveState(i > 0, i < Devices.Count - 1);
    }

    /// <summary>Puts the cards back in the order the user last arranged them.</summary>
    private void ApplySavedOrder()
    {
        if (_settings.DeviceOrder.Count == 0) return;

        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < _settings.DeviceOrder.Count; i++)
            rank[_settings.DeviceOrder[i]] = i;

        List<DeviceViewModel> ordered = Devices
            .OrderBy(d => rank.TryGetValue(d.Id, out int r) ? r : int.MaxValue)
            .ToList();

        for (int i = 0; i < ordered.Count; i++)
            Devices.Move(Devices.IndexOf(ordered[i]), i);
    }

    private void OnDeviceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceViewModel.Channel))
        {
            // A surround position needs the side signal; global mono would flatten it.
            if (MonoOutput && sender is DeviceViewModel device && device.Channel.IsSurroundPosition())
                MonoOutput = false;
            return;
        }

        if (e.PropertyName != nameof(DeviceViewModel.IsEnabled)) return;
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HintText));
        OnPropertyChanged(nameof(ShowBluetoothWarning));
        OnPropertyChanged(nameof(BluetoothWarningText));
        UpdateStatus();
    }

    public void Save()
    {
        var settings = new AppSettings
        {
            Source = _engine.Source.ToString(),
            MasterVolume = 100,
            PerfectSync = _perfectSync,
            MonoOutput = _monoOutput,
            AutoStart = _autoStart,
            LaunchAtLogin = _launchAtLogin,
            LatencyMode = _latencyMode.ToString(),
            LowLatency = _latencyMode == LatencyMode.Latency,
            CalibrationMicId = _selectedMicrophone?.Id ?? string.Empty,
            WindowLeft = _settings.WindowLeft,
            WindowTop = _settings.WindowTop,
            WindowWidth = _settings.WindowWidth,
            WindowHeight = _settings.WindowHeight,
            WindowMaximized = _settings.WindowMaximized,
            DeviceOrder = Devices.Select(d => d.Id).ToList()
        };

        // Keep settings for devices that are unplugged right now: a scan-triggered save
        // must not wipe the EQ of a speaker that is simply powered off.
        foreach (KeyValuePair<string, DeviceSettings> saved in _settings.Devices)
            settings.Devices[saved.Key] = saved.Value;

        foreach (DeviceViewModel device in Devices)
            settings.Devices[device.Id] = device.ToSettings();

        SettingsStore.Save(settings);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _uiTimer.Stop();
        _scanTimer.Stop();

        try { Save(); } catch { /* ignore */ }

        // Give the user their normal default output back before we let go of the endpoints.
        try { RestoreSystemDefault(); } catch { /* ignore */ }

        _engine.Dispose();

        foreach (DeviceViewModel device in Devices)
        {
            device.PropertyChanged -= OnDeviceChanged;
            device.Dispose();
        }

        Devices.Clear();

        try { _enumerator.Dispose(); } catch { /* ignore */ }
    }
}
