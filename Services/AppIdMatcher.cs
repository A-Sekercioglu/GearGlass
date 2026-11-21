namespace GearGlass.Services;

/// <summary>
/// Maps a GSMTC SourceAppUserModelId (e.g. "Spotify.exe", "chrome",
/// "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify") to process names that can own
/// the matching Windows audio session.
/// </summary>
public static class AppIdMatcher
{
    private static readonly Dictionary<string, string> KnownAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["308046B0AF4A39CB"] = "firefox",
        ["MSEdge"] = "msedge",
        ["Microsoft.ZuneMusic"] = "Microsoft.Media.Player",
        ["Microsoft.ZuneVideo"] = "Video.UI"
    };

    private static readonly Dictionary<string, string> DisplayAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["308046B0AF4A39CB"] = "Firefox",
        ["MSEdge"] = "Edge",
        ["Microsoft.ZuneMusic"] = "Media Player",
        ["Microsoft.ZuneVideo"] = "Filmler"
    };

    public static IReadOnlyList<string> CandidateNames(string? appId)
    {
        var candidates = new List<string>();
        if (string.IsNullOrWhiteSpace(appId))
        {
            return candidates;
        }

        foreach (var rawPart in appId.Trim().Split('!', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = rawPart;
            var slash = part.LastIndexOfAny(['\\', '/']);
            if (slash >= 0)
            {
                part = part[(slash + 1)..];
            }

            if (part.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                part = part[..^4];
            }

            // Packaged family names carry a publisher hash after the underscore.
            var underscore = part.IndexOf('_');
            if (underscore > 0)
            {
                part = part[..underscore];
            }

            Add(candidates, part);
            if (KnownAliases.TryGetValue(part, out var alias))
            {
                Add(candidates, alias);
            }

            var lastDot = part.LastIndexOf('.');
            if (lastDot > 0 && lastDot < part.Length - 1)
            {
                Add(candidates, part[(lastDot + 1)..]);
            }
        }

        return candidates;
    }

    public static bool Matches(string processName, IReadOnlyList<string> candidates)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        foreach (var candidate in candidates)
        {
            if (string.Equals(candidate, processName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (processName.Length >= 4 &&
                candidate.Contains(processName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string DisplayName(string? appId)
    {
        var candidates = CandidateNames(appId);
        if (candidates.Count == 0)
        {
            return "—";
        }

        var name = candidates[^1];
        foreach (var candidate in candidates)
        {
            if (DisplayAliases.TryGetValue(candidate, out var alias))
            {
                name = alias;
                break;
            }
        }

        name = name.ToUpperInvariant();
        return name.Length > 12 ? name[..12] : name;
    }

    private static void Add(List<string> list, string value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            !list.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(value);
        }
    }
}
