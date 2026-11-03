using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using AudioMesh.Audio;
using AudioMesh.Services;
using NAudio.CoreAudioApi;
using Brush = System.Windows.Media.Brush;
using Point = System.Windows.Point;

namespace AudioMesh.ViewModels;

public sealed class DeviceViewModel : ObservableObject
{
    private static readonly Brush BluetoothBrush = Freeze("#6FA8FF");
    private static readonly Brush HdmiBrush = Freeze("#F2A33A");
    private static readonly Brush UsbBrush = Freeze("#35C58A");
    private static readonly Brush AnalogBrush = Freeze("#9AA6C0");
    private static readonly Brush BluetoothSoft = Freeze("#1A6FA8FF");
    private static readonly Brush HdmiSoft = Freeze("#1AF2A33A");
    private static readonly Brush UsbSoft = Freeze("#1A35C58A");
    private static readonly Brush AnalogSoft = Freeze("#1A9AA6C0");
    private static readonly Brush OkBrush = Freeze("#35C58A");
    private static readonly Brush WarnBrush = Freeze("#F2A33A");
    private static readonly Brush ErrorBrush = Freeze("#FF5F57");
    private static readonly Brush MutedDotBrush = Freeze("#4A5568");
    private static readonly Brush AccentBrush = Freeze("#4C8DFF");

    private readonly AudioEngine _engine;
    private EndpointInfo _info;
    private DeviceChannel? _channel;

    private bool _enabled;
    private bool _desiredEnabled;
    private double _volume = 100;
    private double _delayMs;
    private bool _muted;
    private ChannelMode _channelMode = ChannelMode.Stereo;
    private bool _canMoveUp;
    private bool _canMoveDown;
    private double _level;
    private double _peakHold;
    private string _statusText = "not routed";

    private EqSettings _eq = new();
    private string _eqPreset = "Flat";
    private double _eqBass;
    private double _eqTreble;
    private bool _eqLoudness;
    private double _eqLoudnessAmount = 60;
    private bool _applyingPreset;
    private PointCollection _eqCurve = new();
    private bool _spatialEnabled;
    private double _spatialAmount = 65;

    public DeviceViewModel(EndpointInfo info, AudioEngine engine, DeviceSettings? saved,
                           Action<DeviceViewModel, int>? move = null,
                           Action<DeviceViewModel>? openEq = null)
    {
        _info = info;
        _engine = engine;

        MoveUpCommand = new RelayCommand(_ => move?.Invoke(this, -1));
        MoveDownCommand = new RelayCommand(_ => move?.Invoke(this, 1));
        OpenEqCommand = new RelayCommand(_ => openEq?.Invoke(this));
        ResetEqCommand = new RelayCommand(_ => ResetEq());
        ToggleSpatialCommand = new RelayCommand(_ => SpatialEnabled = !SpatialEnabled);

        _volume = saved?.Volume ?? 100;
        _delayMs = saved?.DelayMs ?? 0;
        _muted = saved?.Muted ?? false;
        _desiredEnabled = saved?.Enabled == true;
        _spatialEnabled = saved?.Spatial == true;
        _spatialAmount = Math.Clamp(saved?.SpatialAmount ?? 65, 0, 100);

        if (Enum.TryParse(saved?.Channel, true, out ChannelMode savedChannel))
            _channelMode = savedChannel;

        _eq = saved?.Eq?.Clone() ?? new EqSettings();
        _eq.Normalize();
        _eqPreset = _eq.Preset;
        _eqBass = _eq.Bass;
        _eqTreble = _eq.Treble;
        _eqLoudness = _eq.Loudness;
        _eqLoudnessAmount = _eq.LoudnessAmount;

        for (int i = 0; i < EqSettings.BandCount; i++)
        {
            var band = new EqBandViewModel(
                EqSettings.Frequencies[i], FormatFrequency(EqSettings.Frequencies[i]), _eq.Bands[i]);
            band.Changed += OnEqBandChanged;
            EqBands.Add(band);
        }

        UpdateEqCurve();

        if (_desiredEnabled && CanRoute)
            SetEnabled(true);
        else if (!CanRoute)
            StatusText = "capture source — plays natively, not re-routed";
    }

    public string Id => _info.Id;
    public string Name => _info.Name;
    public string Kind => _info.Kind;
    public bool IsDefault => _info.IsDefault;
    public bool IsBluetooth => _info.IsBluetooth;
    public bool IsDesiredEnabled => _desiredEnabled;
    public bool IsLocked => _channel is { IsRunning: true } channel && channel.FillMs >= 20;

    /// <summary>True while WASAPI is actually rendering this endpoint.</summary>
    public bool IsLive => _channel is { IsRunning: true };

    /// <summary>Last WASAPI failure reported by this endpoint, if any.</summary>
    public string? LastError => _channel?.LastError;

    /// <summary>Current elastic-buffer fill in milliseconds (0 when not routed).</summary>
    public double BufferMs => _channel?.FillMs ?? 0;

    public int TargetBufferMs => _channel?.TargetBufferMs ?? 0;

    public double DriftPpm => _channel?.DriftPpm ?? 0;
    public int Rate => _info.Rate;
    public int Channels => _info.Channels;

    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand OpenEqCommand { get; }
    public RelayCommand ResetEqCommand { get; }
    public RelayCommand ToggleSpatialCommand { get; }

    public ObservableCollection<EqBandViewModel> EqBands { get; } = new();

    public bool CanMoveUp
    {
        get => _canMoveUp;
        private set => Set(ref _canMoveUp, value);
    }

    public bool CanMoveDown
    {
        get => _canMoveDown;
        private set => Set(ref _canMoveDown, value);
    }

    public void SetMoveState(bool up, bool down)
    {
        CanMoveUp = up;
        CanMoveDown = down;
    }

    public Brush BadgeBrush => Kind switch
    {
        "Bluetooth" => BluetoothBrush,
        "HDMI" => HdmiBrush,
        "USB" => UsbBrush,
        _ => AnalogBrush
    };

    public Brush BadgeSoftBrush => Kind switch
    {
        "Bluetooth" => BluetoothSoft,
        "HDMI" => HdmiSoft,
        "USB" => UsbSoft,
        _ => AnalogSoft
    };

    public string TypeGlyph => Kind switch
    {
        "Bluetooth" => "\uE702",
        "HDMI" => "\uE7F4",
        "USB" => "\uE88E",
        "Analog" => "\uE7F5",
        _ => "\uE8D6"
    };

    public string DetailText =>
        $"{Kind} · {Rate / 1000.0:0.#} kHz · {Channels} ch" +
        (IsBluetooth ? " · A2DP" : string.Empty);

    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;

            if (value && !CanRoute)
            {
                // It is the device we are capturing: it already plays natively.
                // Routing to it would double the audio and create a feedback loop.
                StatusText = "capture source — plays natively, not re-routed";
                OnPropertyChanged();
                return;
            }

            SetEnabled(value);
            _desiredEnabled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// False for the default endpoint while capturing system audio. Loopback capture
    /// records that endpoint's output, so rendering to it as well would feed our own
    /// signal back into the capture and repeat forever.
    /// </summary>
    public bool CanRoute => !(IsDefault && _engine.Source == SourceKind.SystemAudio);

    public double Volume
    {
        get => _volume;
        set
        {
            if (!Set(ref _volume, Math.Clamp(value, 0, 100))) return;
            if (_channel != null) _channel.Volume = (float)Math.Pow(_volume / 100.0, 1.6);
            UpdateEqCurve();
            OnPropertyChanged(nameof(EqPreampText));
        }
    }

    public double DelayMs
    {
        get => _delayMs;
        set
        {
            if (!Set(ref _delayMs, Math.Clamp(value, 0, 1000))) return;
            if (_channel != null) _channel.DelayMs = (int)_delayMs;
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            if (!Set(ref _muted, value)) return;
            if (_channel != null) _channel.Muted = _muted;
        }
    }

    /// <summary>
    /// How this endpoint renders the shared stream. <see cref="ChannelMode.Left"/> and
    /// <see cref="ChannelMode.Right"/> turn two separate speakers into a stereo pair.
    /// </summary>
    public ChannelMode Channel
    {
        get => _channelMode;
        set
        {
            if (!Set(ref _channelMode, value)) return;
            if (_channel != null) _channel.Channel = value;
            OnPropertyChanged(nameof(ChannelHint));
            OnPropertyChanged(nameof(SupportsSpatial));
            OnPropertyChanged(nameof(SpatialTooltip));
        }
    }

    public string ChannelHint =>
        ChannelOption.All.FirstOrDefault(o => o.Mode == _channelMode)?.Hint ?? string.Empty;

    /// <summary>Virtual surround widening on this speaker (only meaningful in Stereo).</summary>
    public bool SpatialEnabled
    {
        get => _spatialEnabled;
        set
        {
            if (!Set(ref _spatialEnabled, value)) return;
            if (_channel != null) _channel.SpatialEnabled = value;
            OnPropertyChanged(nameof(SpatialSummary));
            OnPropertyChanged(nameof(SpatialBrush));
            OnPropertyChanged(nameof(SpatialTooltip));
        }
    }

    public double SpatialAmount
    {
        get => _spatialAmount;
        set
        {
            if (!Set(ref _spatialAmount, Math.Clamp(value, 0, 100))) return;
            if (_channel != null) _channel.SpatialAmount = _spatialAmount;
            OnPropertyChanged(nameof(SpatialSummary));
            OnPropertyChanged(nameof(SpatialTooltip));
        }
    }

    public string SpatialSummary => _spatialEnabled ? $"{_spatialAmount:0}%" : "Off";

    public Brush SpatialBrush => _spatialEnabled ? AccentBrush : MutedDotBrush;

    public bool SupportsSpatial => _channelMode == ChannelMode.Stereo && !_engine.MonoOutput;

    public string SpatialTooltip
    {
        get
        {
            if (_engine.MonoOutput)
                return "Virtual surround needs stereo output — turn Mono off first";
            if (_channelMode != ChannelMode.Stereo)
                return "Virtual surround needs the Stereo channel mode";

            return $"Virtual surround width {_spatialAmount:0}% — click to " +
                   (_spatialEnabled ? "turn it off" : "turn it on");
        }
    }

    public void RefreshSpatialState()
    {
        OnPropertyChanged(nameof(SupportsSpatial));
        OnPropertyChanged(nameof(SpatialTooltip));
    }

    public string[] EqPresets => EqSettings.Presets;

    public string EqPreset
    {
        get => _eqPreset;
        set
        {
            if (!Set(ref _eqPreset, value)) return;

            if (value != "Custom") ApplyPreset(value);

            OnPropertyChanged(nameof(EqSummary));
        }
    }

    public double Bass
    {
        get => _eqBass;
        set
        {
            if (!Set(ref _eqBass, Math.Clamp(value, -12, 12))) return;
            MarkCustom();
            PushEq();
        }
    }

    public double Treble
    {
        get => _eqTreble;
        set
        {
            if (!Set(ref _eqTreble, Math.Clamp(value, -12, 12))) return;
            MarkCustom();
            PushEq();
        }
    }

    public bool Loudness
    {
        get => _eqLoudness;
        set
        {
            if (!Set(ref _eqLoudness, value)) return;
            MarkCustom();
            PushEq();
        }
    }

    public double LoudnessAmount
    {
        get => _eqLoudnessAmount;
        set
        {
            if (!Set(ref _eqLoudnessAmount, Math.Clamp(value, 0, 100))) return;
            MarkCustom();
            PushEq();
        }
    }

    public PointCollection EqCurve
    {
        get => _eqCurve;
        private set => Set(ref _eqCurve, value);
    }

    public string EqSummary
    {
        get
        {
            if (BuildEq().IsFlat) return "Flat";
            return _eqPreset == "Custom" ? "Custom" : _eqPreset;
        }
    }

    public string EqPreampText
    {
        get
        {
            double db = Equalizer.PreampDb(BuildEq(), ChannelVolume);
            return db < -0.05 ? $"{db:0.0} dB auto preamp" : "No preamp";
        }
    }

    private void OnEqBandChanged()
    {
        if (_applyingPreset) return;
        MarkCustom();
        PushEq();
    }

    private void MarkCustom()
    {
        if (_eqPreset == "Custom") return;
        _eqPreset = "Custom";
        OnPropertyChanged(nameof(EqPreset));
    }

    private void ApplyPreset(string preset)
    {
        EqSettings source = EqSettings.Create(preset);

        _applyingPreset = true;
        try
        {
            for (int i = 0; i < EqBands.Count && i < EqSettings.BandCount; i++)
                EqBands[i].Gain = source.Bands[i];
        }
        finally
        {
            _applyingPreset = false;
        }

        PushEq();
        OnPropertyChanged(nameof(EqPreset));
    }

    private void PushEq()
    {
        _eq = BuildEq();
        _channel?.SetEq(_eq);
        UpdateEqCurve();
        OnPropertyChanged(nameof(EqSummary));
        OnPropertyChanged(nameof(EqPreampText));
    }

    private void UpdateEqCurve()
    {
        ResponsePoint[] points = Equalizer.BuildCurve(BuildEq(), ChannelRate, 780, 120, ChannelVolume);
        var curve = new PointCollection(points.Length);
        foreach (ResponsePoint point in points)
            curve.Add(new Point(point.X, point.Y));
        curve.Freeze();
        EqCurve = curve;
    }

    private EqSettings BuildEq()
    {
        var eq = new EqSettings
        {
            Preset = _eqPreset,
            Bass = _eqBass,
            Treble = _eqTreble,
            Loudness = _eqLoudness,
            LoudnessAmount = _eqLoudnessAmount
        };

        for (int i = 0; i < EqBands.Count && i < EqSettings.BandCount; i++)
            eq.Bands[i] = EqBands[i].Gain;

        return eq;
    }

    private void ResetEq()
    {
        _applyingPreset = true;
        try
        {
            _eqPreset = "Flat";
            foreach (EqBandViewModel band in EqBands) band.Gain = 0;
            _eqBass = 0;
            _eqTreble = 0;
            _eqLoudness = false;
            _eqLoudnessAmount = 60;
        }
        finally
        {
            _applyingPreset = false;
        }

        OnPropertyChanged(nameof(EqPreset));
        OnPropertyChanged(nameof(Bass));
        OnPropertyChanged(nameof(Treble));
        OnPropertyChanged(nameof(Loudness));
        OnPropertyChanged(nameof(LoudnessAmount));
        PushEq();
    }

    private int ChannelRate => _channel?.OutputRate ?? (_info.Rate > 0 ? _info.Rate : 48000);

    private double ChannelVolume => Math.Pow(_volume / 100.0, 1.6);

    private static string FormatFrequency(double frequency) =>
        frequency >= 1000 ? (frequency / 1000.0).ToString("0.#") + "k" : frequency.ToString("0");

    public double Level
    {
        get => _level;
        private set
        {
            if (!Set(ref _level, value)) return;
            OnPropertyChanged(nameof(LevelWidth));
        }
    }

    public double LevelWidth => Math.Min(1.0, _level) * 140;

    public double PeakHoldWidth => Math.Min(1.0, _peakHold) * 140;

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (!Set(ref _statusText, value)) return;
            OnPropertyChanged(nameof(StatusBrush));
        }
    }

    public Brush StatusBrush
    {
        get
        {
            if (_statusText.StartsWith("error", StringComparison.OrdinalIgnoreCase)) return ErrorBrush;
            if (_statusText.Contains("buffer")) return OkBrush;
            if (_statusText.Contains("starting") || _statusText.Contains("waiting")) return WarnBrush;
            return MutedDotBrush;
        }
    }

    public DeviceSettings ToSettings() => new()
    {
        Enabled = _desiredEnabled,
        Volume = _volume,
        DelayMs = (int)_delayMs,
        Muted = _muted,
        Channel = _channelMode.ToString(),
        Spatial = _spatialEnabled,
        SpatialAmount = _spatialAmount,
        Eq = BuildEq()
    };

    private void SetEnabled(bool value)
    {
        _enabled = value;

        if (value)
        {
            _channel = _engine.EnableDevice(_info.Device, IsBluetooth);
            _channel.Volume = (float)Math.Pow(_volume / 100.0, 1.6);
            _channel.DelayMs = (int)_delayMs;
            _channel.Muted = _muted;
            _channel.Channel = _channelMode;
            _channel.SpatialEnabled = _spatialEnabled;
            _channel.SpatialAmount = _spatialAmount;
            _channel.SetEq(BuildEq());
            StatusText = "waiting for stream";
        }
        else
        {
            _engine.DisableDevice(Id);
            _channel = null;
            Level = 0;
            _peakHold = 0;
            OnPropertyChanged(nameof(PeakHoldWidth));
            StatusText = "not routed";
        }
    }

    public void Refresh()
    {
        var channel = _channel;
        if (channel == null)
        {
            if (_level > 0) Level = 0;
            if (_peakHold > 0)
            {
                _peakHold = 0;
                OnPropertyChanged(nameof(PeakHoldWidth));
            }
            return;
        }

        double peak = channel.TakePeak();
        Level = Math.Max(peak, _level * 0.72);

        double hold = Math.Max(peak, _peakHold * 0.94);
        if (Math.Abs(hold - _peakHold) > 0.0005)
        {
            _peakHold = hold;
            OnPropertyChanged(nameof(PeakHoldWidth));
        }

        if (channel.LastError != null)
        {
            StatusText = "error: " + channel.LastError;
            return;
        }

        if (!channel.IsRunning)
        {
            StatusText = "starting…";
            return;
        }

        string text = $"{channel.FillMs:0} ms buffer · drift {channel.DriftPpm:+0;-0;0} ppm";

        long underruns = channel.Underruns;
        if (underruns > 0) text += $" · {underruns} dropouts";

        StatusText = text;
    }

    /// <summary>
    /// Merges freshly enumerated metadata (name, default flag, format) into this card.
    /// The MMDevice instance the running channel was opened with is kept, because the
    /// WASAPI stream holds it; only the descriptive fields are refreshed.
    /// </summary>
    public void UpdateMeta(EndpointSnapshot fresh)
    {
        MMDevice keep = _info.Device;

        _info = new EndpointInfo
        {
            Device = keep,
            Name = fresh.Name,
            Kind = fresh.Kind,
            IsDefault = fresh.IsDefault,
            IsBluetooth = fresh.IsBluetooth,
            Rate = fresh.Rate,
            Channels = fresh.Channels
        };

        OnSourceChanged();
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(IsDefault));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(BadgeBrush));
        OnPropertyChanged(nameof(BadgeSoftBrush));
        OnPropertyChanged(nameof(TypeGlyph));
    }

    public void Detach()
    {
        if (_channel != null) _engine.DisableDevice(Id);
        _channel = null;
    }

    /// <summary>Re-evaluates routability after the capture source or default device changed.</summary>
    public void OnSourceChanged()
    {
        OnPropertyChanged(nameof(CanRoute));

        if (!CanRoute && _enabled)
        {
            SetEnabled(false);
            OnPropertyChanged(nameof(IsEnabled));
        }
        else if (CanRoute && _desiredEnabled && !_enabled)
        {
            // The user asked for this endpoint earlier; it is routable again now.
            SetEnabled(true);
            OnPropertyChanged(nameof(IsEnabled));
        }

        if (!CanRoute)
            StatusText = "capture source — plays natively, not re-routed";
        else if (!_enabled)
            StatusText = "not routed";
    }

    public void Dispose()
    {
        Detach();
        _info.Dispose();
    }

    private static Brush Freeze(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
