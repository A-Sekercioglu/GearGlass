using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace GearGlass.Services;

/// <summary>
/// Reads and sets the per-application audio session volume of the active media
/// app on the default playback device. The system master volume is never touched.
/// Calls are serialized because Read and SetVolume run on different worker threads.
/// </summary>
public sealed class AppVolumeService : IDisposable
{
    private readonly object _sync = new();
    private MMDeviceEnumerator? _enumerator;
    private bool _disposed;

    public AppVolumeReading Read(string? appIdHint)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return new AppVolumeReading(false, 0, "SES —");
            }

            var candidates = AppIdMatcher.CandidateNames(appIdHint);
            if (candidates.Count == 0)
            {
                return new AppVolumeReading(false, 0, "SES —");
            }

            try
            {
                using var device = GetDefaultDevice();
                var sessions = FindSessions(device, candidates);
                try
                {
                    if (sessions.Count == 0)
                    {
                        return new AppVolumeReading(false, 0, "SES YOK");
                    }

                    var percent = Math.Round(sessions[0].SimpleAudioVolume.Volume * 100.0);
                    return new AppVolumeReading(true, percent, $"SES {percent:0}%");
                }
                finally
                {
                    DisposeAll(sessions);
                }
            }
            catch (Exception error) when (error is COMException or InvalidOperationException)
            {
                ResetEnumerator();
                return new AppVolumeReading(false, 0, "SES HATA");
            }
        }
    }

    public bool SetVolume(string? appIdHint, double percent)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return false;
            }

            var candidates = AppIdMatcher.CandidateNames(appIdHint);
            if (candidates.Count == 0)
            {
                return false;
            }

            try
            {
                using var device = GetDefaultDevice();
                var sessions = FindSessions(device, candidates);
                try
                {
                    var level = (float)Math.Clamp(percent / 100.0, 0, 1);
                    foreach (var session in sessions)
                    {
                        session.SimpleAudioVolume.Volume = level;
                    }

                    return sessions.Count > 0;
                }
                finally
                {
                    DisposeAll(sessions);
                }
            }
            catch (Exception error) when (error is COMException or InvalidOperationException)
            {
                ResetEnumerator();
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            ResetEnumerator();
        }
    }

    private MMDevice GetDefaultDevice()
    {
        _enumerator ??= new MMDeviceEnumerator();
        return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    private static List<AudioSessionControl> FindSessions(MMDevice device, IReadOnlyList<string> candidates)
    {
        var matches = new List<AudioSessionControl>();
        var manager = device.AudioSessionManager;
        manager.RefreshSessions();
        var sessions = manager.Sessions;
        for (var i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            if (!session.IsSystemSoundsSession &&
                AppIdMatcher.Matches(GetProcessName(session.GetProcessID), candidates))
            {
                matches.Add(session);
            }
            else
            {
                session.Dispose();
            }
        }

        return matches;
    }

    private static string GetProcessName(uint processId)
    {
        if (processId == 0)
        {
            return string.Empty;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static void DisposeAll(List<AudioSessionControl> sessions)
    {
        foreach (var session in sessions)
        {
            session.Dispose();
        }
    }

    private void ResetEnumerator()
    {
        _enumerator?.Dispose();
        _enumerator = null;
    }
}
