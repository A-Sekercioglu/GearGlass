using GearGlass.Services;
using GearGlass.ViewModels;

var failures = 0;
var passed = 0;

void Check(string name, bool condition)
{
    if (condition)
    {
        passed++;
    }
    else
    {
        failures++;
        Console.Error.WriteLine($"FAIL: {name}");
    }
}

// --- Volume slider must not echo values read from Windows back as writes ---
{
    var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    var vm = new HudViewModel(() => now);
    var writes = new List<double>();
    vm.VolumeChangeRequested += writes.Add;

    vm.ApplyAppVolume(new AppVolumeReading(true, 40, "SES 40%"));
    Check("service volume is applied", vm.AppVolumePercent == 40);
    Check("service volume does not trigger a write", writes.Count == 0);

    vm.AppVolumePercent = 70;
    Check("user volume triggers a write", writes.SequenceEqual([70d]));

    now = now.AddSeconds(0.5);
    vm.ApplyAppVolume(new AppVolumeReading(true, 40, "SES 40%"));
    Check("stale read does not override a recent user change", vm.AppVolumePercent == 70);

    now = now.AddSeconds(2);
    vm.ApplyAppVolume(new AppVolumeReading(true, 65, "SES 65%"));
    Check("read applies after the hold time", vm.AppVolumePercent == 65);
    Check("still only one write", writes.Count == 1);

    vm.ApplyAppVolume(new AppVolumeReading(false, 0, "SES YOK"));
    vm.AppVolumePercent = 10;
    Check("no write while volume is unavailable", writes.Count == 1);
}

// --- Focus gear cycles through CPU, GPU, BELLEK in both directions ---
{
    var vm = new HudViewModel();
    vm.ApplySystemSnapshot(new SystemSnapshot(12.4, 55.6, 8UL << 30, 16UL << 30, null));
    Check("initial focus is CPU", vm.FocusLabel == "CPU" && vm.FocusValue == "12%");
    vm.CycleFocus(1);
    Check("next focus is GPU", vm.FocusLabel == "GPU" && vm.FocusValue == "56%");
    vm.CycleFocus(1);
    Check("next focus is memory", vm.FocusLabel == "BELLEK" && vm.FocusValue == "8.0/16 GB");
    vm.CycleFocus(1);
    Check("focus wraps forward", vm.FocusLabel == "CPU");
    vm.CycleFocus(-1);
    Check("focus wraps backward", vm.FocusLabel == "BELLEK");

    vm.ApplySystemSnapshot(new SystemSnapshot(1, null, 0, 0, "GPU sayaçları desteklenmiyor"));
    Check("missing GPU shows a dash", vm.GpuUsageText == "—");
    Check("GPU status is shown", vm.SystemStatus == "GPU sayaçları desteklenmiyor");
}

// --- System panel toggle glyph ---
{
    var vm = new HudViewModel();
    Check("panel starts expanded", vm.IsSystemPanelExpanded && vm.ToggleGlyph == "▴");
    vm.IsSystemPanelExpanded = false;
    Check("collapsed glyph", vm.ToggleGlyph == "▾");
}

// --- Media session mapping ---
{
    var vm = new HudViewModel();
    vm.ApplyMediaResult(new MediaReadResult(
        new NowPlayingSnapshot("Song", "Artist", "Album", "Spotify.exe", MediaPlaybackState.Playing), null));
    Check("title", vm.TrackTitle == "Song");
    Check("subtitle", vm.TrackSubtitle == "Artist — Album");
    Check("source", vm.MediaSource == "SPOTIFY");
    Check("pause glyph while playing", vm.PlaybackControlGlyph == "⏸");
    Check("hint", vm.MediaSourceHint == "Spotify.exe");

    vm.ApplyMediaResult(new MediaReadResult(null, "Medya oturumu yok"));
    Check("no session clears the hint", vm.MediaSourceHint is null && vm.CurrentSession is null);
    Check("no session shows the status", vm.TrackSubtitle == "Medya oturumu yok");
}

// --- App id to audio-session process matching ---
{
    Check("exe id", AppIdMatcher.Matches("Spotify", AppIdMatcher.CandidateNames("Spotify.exe")));
    Check("plain id", AppIdMatcher.Matches("chrome", AppIdMatcher.CandidateNames("Chrome")));
    Check("edge alias", AppIdMatcher.Matches("msedge", AppIdMatcher.CandidateNames("MSEdge")));
    Check("firefox alias", AppIdMatcher.Matches("firefox", AppIdMatcher.CandidateNames("308046B0AF4A39CB")));
    Check("packaged id",
        AppIdMatcher.Matches("Spotify", AppIdMatcher.CandidateNames("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify")));
    Check("path id", AppIdMatcher.Matches("vlc", AppIdMatcher.CandidateNames(@"C:\Program Files\VideoLAN\VLC\vlc.exe")));
    Check("no false match", !AppIdMatcher.Matches("discord", AppIdMatcher.CandidateNames("Spotify.exe")));
    Check("empty id", AppIdMatcher.CandidateNames(null).Count == 0 && !AppIdMatcher.Matches("x", []));
    Check("firefox display", AppIdMatcher.DisplayName("308046B0AF4A39CB") == "FIREFOX");
    Check("packaged display",
        AppIdMatcher.DisplayName("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify") == "SPOTIFY");
}

// --- Spectrum analyzer: frequency bands, loudness and beat detection ---
{
    const int rate = 48000;
    float[] Tone(double frequency, double amplitude, int count, int offset = 0) =>
        Enumerable.Range(offset, count)
            .Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / rate)))
            .ToArray();

    var silence = new SpectrumAnalyzer(rate).Process(new float[4096]);
    Check("silence produces a frame", silence is not null);
    Check("silence is quiet", silence!.Level == 0 && silence.Bands.All(b => b == 0));
    Check("silence has no beat", silence.BeatCount == 0);

    var analyzer = new SpectrumAnalyzer(rate);
    var tone = analyzer.Process(Tone(1000, 0.5, 4096));
    var (low, high) = analyzer.BandRange(tone!.DominantBand);
    Check("1 kHz lands in its band", low <= 1000 && high >= 1000);
    Check("tone is loud", tone.Level > 0.7);
    Check("band count", tone.Bands.Length == SpectrumAnalyzer.BandCount);

    var highTone = new SpectrumAnalyzer(rate).Process(Tone(8000, 0.5, 4096));
    Check("higher tone, higher band", highTone!.DominantBand > tone.DominantBand);

    var hop = new SpectrumAnalyzer(rate);
    var frames = 0;
    for (var i = 0; i < 8; i++)
    {
        frames += hop.Process(new float[SpectrumAnalyzer.HopSize]) is null ? 0 : 1;
    }

    Check("one frame per hop once the window is full", frames == 7);

    // Kick drum pattern: 80 ms of 60 Hz every 500 ms, silence between.
    var beats = new SpectrumAnalyzer(rate);
    for (var kick = 0; kick < 6; kick++)
    {
        beats.Process(Tone(60, 0.8, rate * 80 / 1000));
        beats.Process(new float[rate * 420 / 1000]);
    }

    Check("kicks are detected as beats", beats.BeatCount is >= 5 and <= 6);

    var steady = new SpectrumAnalyzer(rate);
    steady.Process(Tone(60, 0.8, rate * 3));
    Check("a steady tone is not a stream of beats", steady.BeatCount <= 1);

    var noisy = new SpectrumAnalyzer(rate);
    Check("NaN samples are ignored",
        noisy.Process(Enumerable.Repeat(float.NaN, 2048).ToArray())!.Level == 0);
}

// --- Visualizer view model: smoothing, beats and modes ---
{
    SpectrumFrame Frame(float value, long beats) =>
        new(Enumerable.Repeat(value, SpectrumAnalyzer.BandCount).ToArray(), value, 12, beats, 0);

    var vm = new VisualizerViewModel();
    var changes = new List<string?>();
    vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

    Check("starts in sine mode", vm.Mode == VisualizerMode.Sine && vm.ModeLabel == "SİNÜS");
    vm.CycleModeCommand.Execute(null);
    Check("click goes to bars", vm.Mode == VisualizerMode.Bars && vm.ModeLabel == "ÇUBUK");
    vm.CycleMode();
    Check("then line", vm.Mode == VisualizerMode.Line && vm.ModeLabel == "ÇİZGİ");
    vm.CycleMode();
    Check("and back to sine", vm.Mode == VisualizerMode.Sine);
    Check("snapshot follows the mode", vm.Snapshot.Mode == VisualizerMode.Sine);

    vm.Advance(1 / 60.0, Frame(0.9f, 0));
    Check("audio is active", vm.IsAudioActive);
    Check("bars rise quickly", vm.Snapshot.Values[0] > 0.3);
    for (var i = 0; i < 30; i++)
    {
        vm.Advance(1 / 60.0, Frame(0.9f, 0));
    }

    Check("bars reach the target", Math.Abs(vm.Snapshot.Values[0] - 0.9) < 0.01);
    var loudAmplitude = vm.Snapshot.Amplitude;
    Check("loud music, big wave", loudAmplitude > 0.6);
    Check("first frame does not count as a beat", vm.Snapshot.Pulse == 0);

    vm.Advance(1 / 60.0, Frame(0.9f, 1));
    Check("a new beat flashes", vm.Snapshot.Pulse > 0.9);
    for (var i = 0; i < 30; i++)
    {
        vm.Advance(1 / 60.0, Frame(0.9f, 1));
    }

    Check("the flash fades", vm.Snapshot.Pulse == 0);

    var phaseBefore = vm.Snapshot.Phase;
    vm.Advance(1 / 60.0, null);
    Check("silence deactivates audio", !vm.IsAudioActive);
    Check("the wave keeps moving", vm.Snapshot.Phase != phaseBefore);
    Check("bars fall slowly", vm.Snapshot.Values[0] is > 0.8 and < 0.9);
    for (var i = 0; i < 120; i++)
    {
        vm.Advance(1 / 60.0, null);
    }

    Check("bars settle to zero", vm.Snapshot.Values.All(v => v == 0));
    Check("idle wave stays visible", vm.Snapshot.Amplitude > 0 && vm.Snapshot.Amplitude < 0.1);

    vm.Advance(5, Frame(1, 1));
    Check("a long pause does not jump", vm.Snapshot.Values[0] < 1.01);
    Check("snapshot is raised", changes.Contains(nameof(VisualizerViewModel.Snapshot)));

    var hud = new HudViewModel();
    Check("hud exposes the visualizer", hud.Visualizer is not null);
}

Console.WriteLine($"{passed} passed, {failures} failed");
return failures == 0 ? 0 : 1;
