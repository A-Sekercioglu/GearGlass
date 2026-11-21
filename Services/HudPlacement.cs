using System.IO;
using System.Text.Json;

namespace GearGlass.Services;

/// <summary>Where the HUD sits on screen, in WPF device-independent pixels.</summary>
public readonly record struct HudPosition(double Left, double Top);

/// <summary>A rectangle in device-independent pixels (the virtual screen, for example).</summary>
public readonly record struct ScreenArea(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;
}

public static class HudPlacement
{
    public static readonly HudPosition Default = new(12, 12);

    /// <summary>
    /// Keeps a HUD of the given size fully inside the screen area, so a position saved on a
    /// monitor that is no longer connected, or a drag past the edge, never hides the HUD.
    /// </summary>
    public static HudPosition Clamp(HudPosition position, double width, double height, ScreenArea screen)
    {
        if (!double.IsFinite(position.Left) || !double.IsFinite(position.Top))
        {
            position = Default;
        }

        var maxLeft = Math.Max(screen.Left, screen.Right - width);
        var maxTop = Math.Max(screen.Top, screen.Bottom - height);
        return new HudPosition(
            Math.Clamp(position.Left, screen.Left, maxLeft),
            Math.Clamp(position.Top, screen.Top, maxTop));
    }
}

/// <summary>Reads and writes the HUD position to %AppData%\GearGlass\settings.json.</summary>
public sealed class HudSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public HudSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GearGlass",
            "settings.json");
    }

    public HudPosition? LoadPosition()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var saved = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(_path));
            return saved is { Left: double left, Top: double top } ? new HudPosition(left, top) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing or damaged file just means the default corner.
            return null;
        }
    }

    public void SavePosition(HudPosition position)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(new SavedSettings(position.Left, position.Top), JsonOptions);
            File.WriteAllText(_path, json);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Not being able to remember the position is not worth interrupting the user.
        }
    }

    private sealed record SavedSettings(double? Left, double? Top);
}
