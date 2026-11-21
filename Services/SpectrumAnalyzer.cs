namespace GearGlass.Services;

/// <summary>One analysed slice of audio, normalised for display.</summary>
/// <param name="Bands">Log-spaced band levels, 0..1, lowest frequency first.</param>
/// <param name="Level">Overall loudness, 0..1.</param>
/// <param name="DominantBand">Index of the loudest band.</param>
/// <param name="BeatCount">Total beats detected so far; a change means a new beat.</param>
/// <param name="TimestampMs">Environment.TickCount64 when the frame was produced.</param>
public sealed record SpectrumFrame(
    float[] Bands,
    float Level,
    int DominantBand,
    long BeatCount,
    long TimestampMs);

/// <summary>
/// Turns a mono sample stream into spectrum frames: Hann-windowed FFT, log-spaced
/// bands and a low-frequency energy beat detector. Not thread-safe; feed it from
/// one thread (the audio capture thread).
/// </summary>
public sealed class SpectrumAnalyzer
{
    public const int FftSize = 1024;
    public const int HopSize = 512;
    public const int BandCount = 24;
    public const double MinFrequency = 40;
    public const double MaxFrequency = 16000;

    private const double BandFloorDb = -60;
    private const double LevelFloorDb = -50;
    private const double BeatLowFrequency = 150;
    private const double BeatThreshold = 1.5;
    private const double MinBeatEnergy = 0.0025;
    private const double BeatCooldownSeconds = 0.25;
    private const int EnergyHistoryLength = 43;

    private readonly int _sampleRate;
    private readonly Func<long> _clock;
    private readonly float[] _ring = new float[FftSize];
    private readonly double[] _window = new double[FftSize];
    private readonly double _windowSum;
    private readonly double[] _real = new double[FftSize];
    private readonly double[] _imaginary = new double[FftSize];
    private readonly int[] _bandLow = new int[BandCount];
    private readonly int[] _bandHigh = new int[BandCount];
    private readonly int _beatHighBin;
    private readonly int _beatCooldownFrames;
    private readonly double[] _energyHistory = new double[EnergyHistoryLength];
    private int _historyPosition;
    private int _historyCount;
    private int _ringPosition;
    private int _filled;
    private int _sinceLastFrame;
    private long _beatCount;
    private int _framesSinceBeat = int.MaxValue;

    public SpectrumAnalyzer(int sampleRate, Func<long>? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        _sampleRate = sampleRate;
        _clock = clock ?? (() => Environment.TickCount64);

        for (var i = 0; i < FftSize; i++)
        {
            _window[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1)));
            _windowSum += _window[i];
        }

        var binWidth = (double)sampleRate / FftSize;
        var maxBin = FftSize / 2 - 1;
        var maxFrequency = Math.Min(MaxFrequency, sampleRate / 2.0);
        for (var band = 0; band < BandCount; band++)
        {
            var (low, high) = BandRange(band, maxFrequency);
            var lowBin = Math.Clamp((int)Math.Floor(low / binWidth), 1, maxBin);
            var highBin = Math.Clamp((int)Math.Ceiling(high / binWidth) - 1, lowBin, maxBin);
            _bandLow[band] = lowBin;
            _bandHigh[band] = highBin;
        }

        _beatHighBin = Math.Clamp((int)Math.Ceiling(BeatLowFrequency / binWidth), 1, maxBin);
        _beatCooldownFrames = (int)Math.Ceiling(BeatCooldownSeconds * sampleRate / HopSize);
    }

    public long BeatCount => _beatCount;

    /// <summary>Frequency range in Hz covered by a band.</summary>
    public (double Low, double High) BandRange(int band) =>
        BandRange(band, Math.Min(MaxFrequency, _sampleRate / 2.0));

    /// <summary>Feeds mono samples (-1..1). Returns the newest frame produced, if any.</summary>
    public SpectrumFrame? Process(ReadOnlySpan<float> samples)
    {
        SpectrumFrame? latest = null;
        foreach (var sample in samples)
        {
            _ring[_ringPosition] = float.IsFinite(sample) ? sample : 0;
            _ringPosition = (_ringPosition + 1) % FftSize;
            if (_filled < FftSize)
            {
                _filled++;
            }

            _sinceLastFrame++;
            if (_filled == FftSize && _sinceLastFrame >= HopSize)
            {
                _sinceLastFrame = 0;
                latest = Analyze();
            }
        }

        return latest;
    }

    private static (double Low, double High) BandRange(int band, double maxFrequency)
    {
        var ratio = maxFrequency / MinFrequency;
        return (MinFrequency * Math.Pow(ratio, (double)band / BandCount),
                MinFrequency * Math.Pow(ratio, (double)(band + 1) / BandCount));
    }

    private SpectrumFrame Analyze()
    {
        double sumSquares = 0;
        for (var i = 0; i < FftSize; i++)
        {
            var sample = _ring[(_ringPosition + i) % FftSize];
            sumSquares += sample * sample;
            _real[i] = sample * _window[i];
            _imaginary[i] = 0;
        }

        Fft(_real, _imaginary);

        var bands = new float[BandCount];
        var dominant = 0;
        for (var band = 0; band < BandCount; band++)
        {
            double peak = 0;
            for (var bin = _bandLow[band]; bin <= _bandHigh[band]; bin++)
            {
                peak = Math.Max(peak, Amplitude(bin));
            }

            bands[band] = (float)Normalize(peak, BandFloorDb);
            if (bands[band] > bands[dominant])
            {
                dominant = band;
            }
        }

        var rms = Math.Sqrt(sumSquares / FftSize);
        var level = (float)Normalize(rms, LevelFloorDb);
        DetectBeat();

        return new SpectrumFrame(bands, level, dominant, _beatCount, _clock());
    }

    private void DetectBeat()
    {
        double energy = 0;
        for (var bin = 1; bin <= _beatHighBin; bin++)
        {
            var amplitude = Amplitude(bin);
            energy += amplitude * amplitude;
        }

        double average = 0;
        for (var i = 0; i < _historyCount; i++)
        {
            average += _energyHistory[i];
        }

        average = _historyCount == 0 ? 0 : average / _historyCount;
        if (_framesSinceBeat != int.MaxValue)
        {
            _framesSinceBeat++;
        }

        if (energy > MinBeatEnergy &&
            energy > average * BeatThreshold &&
            _framesSinceBeat >= _beatCooldownFrames)
        {
            _beatCount++;
            _framesSinceBeat = 0;
        }

        _energyHistory[_historyPosition] = energy;
        _historyPosition = (_historyPosition + 1) % EnergyHistoryLength;
        _historyCount = Math.Min(_historyCount + 1, EnergyHistoryLength);
    }

    /// <summary>Peak amplitude of a bin, scaled so a full-scale sine reads 1.0.</summary>
    private double Amplitude(int bin) =>
        2 * Math.Sqrt(_real[bin] * _real[bin] + _imaginary[bin] * _imaginary[bin]) / _windowSum;

    private static double Normalize(double amplitude, double floorDb)
    {
        if (amplitude <= 0)
        {
            return 0;
        }

        var db = 20 * Math.Log10(amplitude);
        return Math.Clamp((db - floorDb) / -floorDb, 0, 1);
    }

    /// <summary>In-place iterative radix-2 FFT.</summary>
    private static void Fft(double[] real, double[] imaginary)
    {
        var n = real.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2 * Math.PI / length;
            var stepReal = Math.Cos(angle);
            var stepImaginary = Math.Sin(angle);
            for (var start = 0; start < n; start += length)
            {
                double wReal = 1, wImaginary = 0;
                for (var k = 0; k < length / 2; k++)
                {
                    var even = start + k;
                    var odd = even + length / 2;
                    var oddReal = real[odd] * wReal - imaginary[odd] * wImaginary;
                    var oddImaginary = real[odd] * wImaginary + imaginary[odd] * wReal;
                    real[odd] = real[even] - oddReal;
                    imaginary[odd] = imaginary[even] - oddImaginary;
                    real[even] += oddReal;
                    imaginary[even] += oddImaginary;
                    var nextReal = wReal * stepReal - wImaginary * stepImaginary;
                    wImaginary = wReal * stepImaginary + wImaginary * stepReal;
                    wReal = nextReal;
                }
            }
        }
    }
}
