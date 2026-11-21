using Windows.Media.Control;

namespace GearGlass.Services;

/// <summary>
/// Reads and controls the current Windows media session through Global System
/// Media Transport Controls (GSMTC). Failures are reported through the result
/// objects instead of being thrown, so a misbehaving player cannot crash the HUD.
/// </summary>
public sealed class MediaSessionService
{
    private readonly SemaphoreSlim _managerGate = new(1, 1);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    public async Task<MediaReadResult> ReadCurrentAsync()
    {
        try
        {
            var session = (await GetManagerAsync()).GetCurrentSession();
            if (session is null)
            {
                return new MediaReadResult(null, "Medya oturumu yok");
            }

            var properties = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            return new MediaReadResult(
                new NowPlayingSnapshot(
                    properties?.Title ?? string.Empty,
                    properties?.Artist ?? string.Empty,
                    properties?.AlbumTitle ?? string.Empty,
                    session.SourceAppUserModelId ?? string.Empty,
                    MapStatus(playback?.PlaybackStatus)),
                null);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The manager can become invalid (e.g. after Explorer restarts); recreate it next time.
            _manager = null;
            return new MediaReadResult(null, "Medya bilgisi okunamadı");
        }
    }

    public async Task<MediaCommandResult> SendCommandAsync(MediaControlCommand command)
    {
        try
        {
            var session = (await GetManagerAsync()).GetCurrentSession();
            if (session is null)
            {
                return new MediaCommandResult(false, "Aktif medya oturumu yok.");
            }

            var accepted = command switch
            {
                MediaControlCommand.PlayPause => await session.TryTogglePlayPauseAsync(),
                MediaControlCommand.Previous => await session.TrySkipPreviousAsync(),
                MediaControlCommand.Next => await session.TrySkipNextAsync(),
                _ => false
            };

            return accepted
                ? new MediaCommandResult(true, null)
                : new MediaCommandResult(false, "Oynatıcı bu komutu desteklemiyor.");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _manager = null;
            return new MediaCommandResult(false, "Medya komutu gönderilemedi.");
        }
    }

    private async Task<GlobalSystemMediaTransportControlsSessionManager> GetManagerAsync()
    {
        var manager = _manager;
        if (manager is not null)
        {
            return manager;
        }

        await _managerGate.WaitAsync();
        try
        {
            return _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        finally
        {
            _managerGate.Release();
        }
    }

    private static MediaPlaybackState MapStatus(
        GlobalSystemMediaTransportControlsSessionPlaybackStatus? status) => status switch
    {
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => MediaPlaybackState.Closed,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => MediaPlaybackState.Opened,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => MediaPlaybackState.Changing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlaybackState.Stopped,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackState.Playing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackState.Paused,
        _ => MediaPlaybackState.Unknown
    };
}
