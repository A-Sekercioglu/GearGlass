using System.Runtime.InteropServices;
using NAudio.Wave;

namespace GearGlass.Services;

/// <summary>
/// Captures what the default playback device is playing (WASAPI loopback) and
/// analyses it into spectrum frames for the visualizer. Captures the whole output
/// mix, not only the media app. Restarts itself through <see cref="EnsureRunning"/>
/// after the device changes or capture stops.
/// </summary>
public sealed class AudioSpectrumService : IDisposable
{
    /// <summary>Frames older than this are treated as silence (loopback sends nothing when idle).</summary>
    private const long StaleFrameMs = 250;

    private readonly object _sync = new();
    private WasapiLoopbackCapture? _capture;
    private volatile SpectrumFrame? _latest;
    private bool _disposed;

    public bool IsCapturing
    {
        get
        {
            lock (_sync)
            {
                return _capture is not null;
            }
        }
    }

    /// <summary>Starts capture if it is not running. Cheap to call repeatedly.</summary>
    public void EnsureRunning()
    {
        lock (_sync)
        {
            if (_disposed || _capture is not null)
            {
                return;
            }

            WasapiLoopbackCapture? capture = null;
            try
            {
                capture = new WasapiLoopbackCapture();
                var format = capture.WaveFormat;
                var analyzer = new SpectrumAnalyzer(format.SampleRate);
                var mono = Array.Empty<float>();
                capture.DataAvailable += (_, e) =>
                {
                    var frames = e.BytesRecorded / Math.Max(1, format.BlockAlign);
                    if (mono.Length < frames)
                    {
                        mono = new float[frames];
                    }

                    var count = ToMono(e.Buffer, e.BytesRecorded, format, mono);
                    var frame = analyzer.Process(mono.AsSpan(0, count));
                    if (frame is not null)
                    {
                        _latest = frame;
                    }
                };
                capture.RecordingStopped += (_, _) => OnCaptureStopped(capture);
                capture.StartRecording();
                _capture = capture;
            }
            catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException)
            {
                capture?.Dispose();
                _capture = null;
            }
        }
    }

    /// <summary>The newest frame, or null when nothing has played recently.</summary>
    public SpectrumFrame? GetLatestFrame()
    {
        var frame = _latest;
        return frame is not null && Environment.TickCount64 - frame.TimestampMs <= StaleFrameMs
            ? frame
            : null;
    }

    public void Dispose()
    {
        WasapiLoopbackCapture? capture;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            capture = _capture;
            _capture = null;
        }

        // Dispose joins the capture thread; do it outside the lock.
        capture?.Dispose();
    }

    private void OnCaptureStopped(WasapiLoopbackCapture capture)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_capture, capture))
            {
                return;
            }

            _capture = null;
        }

        _latest = null;
        capture.Dispose();
    }

    private static int ToMono(byte[] buffer, int bytes, WaveFormat format, float[] destination)
    {
        var channels = Math.Max(1, format.Channels);
        var bytesPerSample = format.BitsPerSample / 8;
        var frames = bytes / Math.Max(1, format.BlockAlign);
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                      (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32);

        for (var frame = 0; frame < frames; frame++)
        {
            float sum = 0;
            var offset = frame * format.BlockAlign;
            for (var channel = 0; channel < channels; channel++)
            {
                var position = offset + channel * bytesPerSample;
                sum += bytesPerSample switch
                {
                    4 when isFloat => BitConverter.ToSingle(buffer, position),
                    4 => BitConverter.ToInt32(buffer, position) / 2147483648f,
                    3 => ((buffer[position] << 8 | buffer[position + 1] << 16 | buffer[position + 2] << 24) >> 8) / 8388608f,
                    2 => BitConverter.ToInt16(buffer, position) / 32768f,
                    _ => 0
                };
            }

            destination[frame] = sum / channels;
        }

        return frames;
    }
}
