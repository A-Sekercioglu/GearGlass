using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using GearGlass.Services;

namespace GearGlass.ViewModels;

public sealed class HudViewModel : INotifyPropertyChanged
{
    private const double BytesPerGigabyte = 1024d * 1024 * 1024;
    private static readonly string[] FocusLabels = ["CPU", "GPU", "BELLEK"];

    /// <summary>
    /// After the user moves the volume slider, periodic reads are ignored for this
    /// long so a slow audio-session read cannot snap the slider back.
    /// </summary>
    public static readonly TimeSpan UserVolumeHoldTime = TimeSpan.FromSeconds(1.5);

    private readonly Func<DateTime> _utcNow;
    private string _trackTitle = "Medya yok";
    private string _trackSubtitle = "Bir oynatıcı başlatın";
    private string _playbackControlGlyph = "▶";
    private string _mediaSource = "—";
    private string _playbackStatus = "BEKLEMEDE";
    private string _appVolumeStatus = "SES —";
    private double _appVolumePercent;
    private bool _appVolumeAvailable;
    private string _systemStatus = string.Empty;
    private string _cpuUsageText = "—";
    private string _gpuUsageText = "—";
    private string _memoryText = "—";
    private bool _isSystemPanelExpanded = true;
    private int _focusIndex;
    private bool _applyingServiceVolume;
    private DateTime _lastUserVolumeChange = DateTime.MinValue;

    public HudViewModel()
        : this(() => DateTime.UtcNow)
    {
    }

    public HudViewModel(Func<DateTime> utcNow)
    {
        _utcNow = utcNow;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public VisualizerViewModel Visualizer { get; } = new();

    /// <summary>Raised only for volume changes made by the user, never for values read from Windows.</summary>
    public event Action<double>? VolumeChangeRequested;

    public string TrackTitle { get => _trackTitle; private set => Set(ref _trackTitle, value); }

    public string TrackSubtitle { get => _trackSubtitle; private set => Set(ref _trackSubtitle, value); }

    public string PlaybackControlGlyph { get => _playbackControlGlyph; private set => Set(ref _playbackControlGlyph, value); }

    public string MediaSource { get => _mediaSource; private set => Set(ref _mediaSource, value); }

    public string PlaybackStatus { get => _playbackStatus; private set => Set(ref _playbackStatus, value); }

    public string AppVolumeStatus { get => _appVolumeStatus; private set => Set(ref _appVolumeStatus, value); }

    public bool AppVolumeAvailable { get => _appVolumeAvailable; private set => Set(ref _appVolumeAvailable, value); }

    public double AppVolumePercent
    {
        get => _appVolumePercent;
        set
        {
            value = Math.Clamp(value, 0, 100);
            if (!Set(ref _appVolumePercent, value) || _applyingServiceVolume)
            {
                return;
            }

            _lastUserVolumeChange = _utcNow();
            AppVolumeStatus = $"SES {value:0}%";
            if (AppVolumeAvailable)
            {
                VolumeChangeRequested?.Invoke(value);
            }
        }
    }

    public string SystemStatus { get => _systemStatus; private set => Set(ref _systemStatus, value); }

    public string CpuUsageText { get => _cpuUsageText; private set => Set(ref _cpuUsageText, value); }

    public string GpuUsageText { get => _gpuUsageText; private set => Set(ref _gpuUsageText, value); }

    public string MemoryText { get => _memoryText; private set => Set(ref _memoryText, value); }

    public string FocusLabel => FocusLabels[_focusIndex];

    public string FocusValue => _focusIndex switch
    {
        0 => CpuUsageText,
        1 => GpuUsageText,
        _ => MemoryText
    };

    public bool IsSystemPanelExpanded
    {
        get => _isSystemPanelExpanded;
        set
        {
            if (Set(ref _isSystemPanelExpanded, value))
            {
                OnPropertyChanged(nameof(ToggleGlyph));
            }
        }
    }

    public string ToggleGlyph => IsSystemPanelExpanded ? "▴" : "▾";

    /// <summary>GSMTC app id of the current session, used to find its audio session.</summary>
    public string? MediaSourceHint { get; private set; }

    public NowPlayingSnapshot? CurrentSession { get; private set; }

    public void ApplySystemSnapshot(SystemSnapshot snapshot)
    {
        CpuUsageText = FormatPercent(snapshot.CpuPercent);
        GpuUsageText = snapshot.GpuPercent is double gpu ? FormatPercent(gpu) : "—";
        MemoryText = snapshot.MemoryTotalBytes == 0
            ? "—"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{snapshot.MemoryUsedBytes / BytesPerGigabyte:0.0}/{snapshot.MemoryTotalBytes / BytesPerGigabyte:0} GB");
        SystemStatus = snapshot.GpuStatus ?? string.Empty;
        OnPropertyChanged(nameof(FocusValue));
    }

    public void ReportSystemError(string message) => SystemStatus = message;

    public void ApplyMediaResult(MediaReadResult result)
    {
        var session = result.Session;
        CurrentSession = session;
        if (session is null)
        {
            MediaSourceHint = null;
            TrackTitle = "Medya yok";
            TrackSubtitle = result.Status ?? "Bir oynatıcı başlatın";
            MediaSource = "—";
            PlaybackStatus = "BEKLEMEDE";
            PlaybackControlGlyph = "▶";
            return;
        }

        MediaSourceHint = session.SourceAppId;
        TrackTitle = string.IsNullOrWhiteSpace(session.Title) ? "Bilinmeyen parça" : session.Title;
        var subtitleParts = new[] { session.Artist, session.AlbumTitle }
            .Where(part => !string.IsNullOrWhiteSpace(part));
        var subtitle = string.Join(" — ", subtitleParts);
        TrackSubtitle = subtitle.Length == 0 ? "Bilinmeyen sanatçı" : subtitle;
        MediaSource = AppIdMatcher.DisplayName(session.SourceAppId);
        PlaybackStatus = session.PlaybackStatus switch
        {
            MediaPlaybackState.Playing => "ÇALIYOR",
            MediaPlaybackState.Paused => "DURAKLATILDI",
            MediaPlaybackState.Stopped => "DURDU",
            MediaPlaybackState.Changing => "DEĞİŞİYOR",
            MediaPlaybackState.Opened => "AÇIK",
            MediaPlaybackState.Closed => "KAPALI",
            _ => "BİLİNMİYOR"
        };
        PlaybackControlGlyph = session.PlaybackStatus == MediaPlaybackState.Playing ? "⏸" : "▶";
    }

    public void ReportMediaControlStatus(string message) => TrackSubtitle = message;

    public void ApplyAppVolume(AppVolumeReading reading)
    {
        AppVolumeAvailable = reading.Available;
        if (!reading.Available)
        {
            AppVolumeStatus = reading.Status;
            return;
        }

        if (_utcNow() - _lastUserVolumeChange < UserVolumeHoldTime)
        {
            // The user is (or just was) dragging the slider; keep their value.
            return;
        }

        AppVolumeStatus = reading.Status;
        _applyingServiceVolume = true;
        try
        {
            AppVolumePercent = reading.Percent;
        }
        finally
        {
            _applyingServiceVolume = false;
        }
    }

    public void CycleFocus(int direction)
    {
        var count = FocusLabels.Length;
        _focusIndex = ((_focusIndex + Math.Sign(direction)) % count + count) % count;
        OnPropertyChanged(nameof(FocusLabel));
        OnPropertyChanged(nameof(FocusValue));
    }

    private static string FormatPercent(double value) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Round(value):0}%");

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        if (propertyName is nameof(CpuUsageText) or nameof(GpuUsageText) or nameof(MemoryText))
        {
            OnPropertyChanged(nameof(FocusValue));
        }

        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
