# GearGlass
.
GearGlass is a compact, always-on-top Windows HUD with a retro gear-inspired theme. It displays the current media session and CPU, GPU, and memory usage.

## Features

- Shows media metadata and offers play/pause and track controls through Windows Global System Media Transport Controls (GSMTC).
- Animates the music in a beat-reactive visualizer with three modes: a single sine wave, a bar chart and a line chart. Click the visualizer strip to switch modes. It analyses the default playback device through WASAPI loopback, so it reacts to everything that is playing.
- Adjusts the active media application's audio-session volume without changing the system master volume.
- Reads CPU and memory usage from Windows APIs and GPU usage from Windows PDH GPU Engine counters.
- Stays above ordinary windows and borderless fullscreen apps without activating itself when clicked.
- Mouse input is enabled by default so HUD controls remain clickable. While a fullscreen app is in the foreground the HUD switches to mouse pass-through automatically.
- Press `Ctrl+Alt+F12` to toggle mouse pass-through manually. The manual choice lasts until the foreground fullscreen state changes.
- Only one copy runs at a time; starting it again does nothing while GearGlass is already open.
- Exclusive fullscreen games may cover ordinary Windows overlays. Use borderless/windowed fullscreen if the HUD is not visible.

## Requirements

- Windows 10 version 2004 (build 19041) or later.
- .NET 10 SDK.

GPU counters depend on driver support. Media controls and metadata are available when the player or browser publishes a Windows media session.

## Run

Open `GearGlass.sln` in Visual Studio, or build everything from the command line:

```powershell
dotnet build .\GearGlass.sln
```

Run the app:

```powershell
dotnet run --project .\GearGlass.App.csproj
```

## Tests

The smoke tests cover the platform-independent view models, spectrum analysis and media-app matching logic, so they also run on Linux and macOS:

```powershell
dotnet run --project .\tests\GearGlass.ToggleSmokeTests.csproj
```

## Project layout

- `MainWindow.xaml(.cs)` – HUD window, overlay behaviour and animations.
- `ViewModels/HudViewModel.cs` – bound state and text formatting.
- `ViewModels/VisualizerViewModel.cs` – visualizer smoothing, beat pulse and mode; `Views/AudioVisualizer.cs` draws its snapshots.
- `Services/SpectrumAnalyzer.cs` – FFT bands and beat detection; `Services/AudioSpectrumService.cs` feeds it from loopback capture.
- `Services/` – CPU, GPU and memory metrics (`SystemMetricsService`), GSMTC media sessions (`MediaSessionService`), per-app volume through NAudio (`AppVolumeService`), and Win32 interop (`OverlayNative`).
- `tests/` – smoke tests.
