namespace GearGlass.Services;

public enum MediaControlCommand
{
    PlayPause,
    Previous,
    Next
}

public enum MediaPlaybackState
{
    Unknown,
    Closed,
    Opened,
    Changing,
    Stopped,
    Playing,
    Paused
}

public sealed record NowPlayingSnapshot(
    string Title,
    string Artist,
    string AlbumTitle,
    string SourceAppId,
    MediaPlaybackState PlaybackStatus);

public sealed record MediaReadResult(NowPlayingSnapshot? Session, string? Status);

public sealed record MediaCommandResult(bool Accepted, string? Status);

public sealed record AppVolumeReading(bool Available, double Percent, string Status);

public sealed record SystemSnapshot(
    double CpuPercent,
    double? GpuPercent,
    ulong MemoryUsedBytes,
    ulong MemoryTotalBytes,
    string? GpuStatus);
