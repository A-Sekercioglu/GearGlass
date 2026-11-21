using System.ComponentModel;
using System.Windows.Input;
using GearGlass.Services;

namespace GearGlass.ViewModels;

public enum VisualizerMode
{
    Sine,
    Bars,
    Line
}

/// <summary>Immutable picture of the visualizer for one rendered frame.</summary>
/// <param name="Values">Smoothed band levels, 0..1.</param>
/// <param name="Amplitude">Sine amplitude as a fraction of half the height, 0..1.</param>
/// <param name="Phase">Sine phase in radians.</param>
/// <param name="Cycles">Number of sine periods across the width.</param>
/// <param name="Pulse">Beat flash, 1 right after a beat, decaying to 0.</param>
public sealed record VisualizerSnapshot(
    VisualizerMode Mode,
    double[] Values,
    double Amplitude,
    double Phase,
    double Cycles,
    double Pulse)
{
    public static VisualizerSnapshot Empty(VisualizerMode mode) =>
        new(mode, new double[SpectrumAnalyzer.BandCount], IdleAmplitude, 0, MinCycles, 0);

    internal const double IdleAmplitude = 0.06;
    internal const double MinCycles = 1.5;
}

/// <summary>
/// Turns raw spectrum frames into smooth, beat-reactive animation state. The view
/// calls <see cref="Advance"/> once per rendered frame and draws <see cref="Snapshot"/>.
/// </summary>
public sealed class VisualizerViewModel : INotifyPropertyChanged
{
    private const double AttackRate = 28;
    private const double DecayPerSecond = 1.8;
    private const double PulseDecayPerSecond = 3.5;
    private const double MaxFrameSeconds = 0.1;

    private readonly double[] _values = new double[SpectrumAnalyzer.BandCount];
    private VisualizerMode _mode;
    private VisualizerSnapshot _snapshot = VisualizerSnapshot.Empty(VisualizerMode.Sine);
    private double _level;
    private double _dominant;
    private double _pulse;
    private double _phase;
    private long _lastBeatCount = -1;
    private bool _isAudioActive;

    public VisualizerViewModel()
    {
        CycleModeCommand = new RelayCommand(CycleMode);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand CycleModeCommand { get; }

    public VisualizerMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
            {
                return;
            }

            _mode = value;
            OnPropertyChanged(nameof(Mode));
            OnPropertyChanged(nameof(ModeLabel));
            PublishSnapshot();
        }
    }

    public string ModeLabel => Mode switch
    {
        VisualizerMode.Sine => "SİNÜS",
        VisualizerMode.Bars => "ÇUBUK",
        _ => "ÇİZGİ"
    };

    public VisualizerSnapshot Snapshot => _snapshot;

    /// <summary>True while audio frames are arriving.</summary>
    public bool IsAudioActive
    {
        get => _isAudioActive;
        private set
        {
            if (_isAudioActive != value)
            {
                _isAudioActive = value;
                OnPropertyChanged(nameof(IsAudioActive));
            }
        }
    }

    public void CycleMode() => Mode = (VisualizerMode)(((int)Mode + 1) % 3);

    /// <summary>Moves the animation forward. <paramref name="frame"/> is null during silence.</summary>
    public void Advance(double elapsedSeconds, SpectrumFrame? frame)
    {
        var dt = Math.Clamp(elapsedSeconds, 0, MaxFrameSeconds);
        IsAudioActive = frame is not null;

        for (var i = 0; i < _values.Length; i++)
        {
            var target = frame is not null && i < frame.Bands.Length ? frame.Bands[i] : 0;
            _values[i] = Approach(_values[i], target, dt);
        }

        _level = Approach(_level, frame?.Level ?? 0, dt);
        var dominantTarget = frame is null ? 0 : (double)frame.DominantBand / (SpectrumAnalyzer.BandCount - 1);
        _dominant += (dominantTarget - _dominant) * Math.Min(1, dt * 4);

        if (frame is not null && frame.BeatCount != _lastBeatCount)
        {
            if (_lastBeatCount >= 0 && frame.BeatCount > _lastBeatCount)
            {
                _pulse = 1;
            }

            _lastBeatCount = frame.BeatCount;
        }

        _pulse = Math.Max(0, _pulse - PulseDecayPerSecond * dt);

        // Louder music scrolls the wave faster; a beat gives it an extra kick.
        var speed = 2 * Math.PI * (0.5 + _level * 2.5 + _pulse * 1.5);
        _phase = (_phase + speed * dt) % (2 * Math.PI);

        PublishSnapshot();
    }

    private static double Approach(double current, double target, double dt) =>
        target > current
            ? current + (target - current) * Math.Min(1, dt * AttackRate)
            : Math.Max(target, current - DecayPerSecond * dt);

    private void PublishSnapshot()
    {
        var amplitude = Math.Clamp(
            VisualizerSnapshot.IdleAmplitude + _level * 0.72 + _pulse * 0.2, 0, 1);
        var cycles = VisualizerSnapshot.MinCycles + _dominant * 3;
        _snapshot = new VisualizerSnapshot(Mode, (double[])_values.Clone(), amplitude, _phase, cycles, _pulse);
        OnPropertyChanged(nameof(Snapshot));
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
