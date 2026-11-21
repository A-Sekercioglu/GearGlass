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

Console.WriteLine($"{passed} passed, {failures} failed");
return failures == 0 ? 0 : 1;
