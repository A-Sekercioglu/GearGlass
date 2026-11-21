# GearGlass

GearGlass is a compact, always-on-top Windows HUD with a retro gear-inspired theme. It displays the current media session and CPU, GPU, and memory usage.

## Features

- Shows media metadata and offers play/pause and track controls through Windows Global System Media Transport Controls (GSMTC).
- Adjusts the active media application's audio-session volume without changing the system master volume.
- Reads CPU and memory usage from Windows APIs and GPU usage from Windows PDH GPU Engine counters.
- Stays above ordinary windows and borderless fullscreen apps without activating itself when clicked.
- Press `Ctrl+Alt+F12` to toggle mouse pass-through. Mouse input is enabled by default so HUD controls remain clickable.
- Exclusive fullscreen games may cover ordinary Windows overlays. Use borderless/windowed fullscreen if the HUD is not visible.

## Requirements

- Windows 10 version 2004 (build 19041) or later.
- .NET 10 SDK.

GPU counters depend on driver support. Media controls and metadata are available when the player or browser publishes a Windows media session.

## Run

```powershell
dotnet run --project .\GearGlass.App.csproj
```

## Tests

Run the WPF smoke tests on Windows:

```powershell
dotnet run --project .\tests\GearGlass.ToggleSmokeTests.csproj
```
