using System.Globalization;
using System.Text.RegularExpressions;
using Headroom.Core.Usage;

namespace Headroom.Core.Providers.Claude;

/// <summary>
/// Parses the text report printed by Claude Code's <c>/usage</c> command, e.g.
/// <c>Current week (all models): 7% used · resets Nov 5, 7pm (Asia/Tokyo)</c>.
/// </summary>
public static partial class ClaudeUsageParser
{
    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|\x1B[@-Z\\-_]")]
    private static partial Regex AnsiEscape();

    // Single-line form: "Current session: 23% used · resets 11pm (Asia/Tokyo)".
    [GeneratedRegex(@"^\s*(?<label>Current [^:]+?)\s*:\s*(?<pct>\d+(?:\.\d+)?)\s*%\s*(?<kind>used|left)\b(?:\s*[·•|-]\s*(?<reset>.+?))?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex InlineWindow();

    // Panel form used by the interactive TUI: a header line, then a percentage line, then a reset line.
    [GeneratedRegex(@"^\s*(?<label>Current (?:session|week)[^:]*?)\s*:?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderLine();

    [GeneratedRegex(@"(?<pct>\d+(?:\.\d+)?)\s*%\s*(?<kind>used|left)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PercentLine();

    [GeneratedRegex(@"^\s*resets?\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex ResetLine();

    private static readonly string[] ErrorMarkers =
    [
        "Failed to load usage data",
        "Invalid API key",
        "Please run /login",
        "not logged in",
        "OAuth token has expired",
        "rate_limit_error",
    ];

    public static string StripAnsi(string text) => AnsiEscape().Replace(text, string.Empty);

    public static UsageSnapshot Parse(string output, DateTimeOffset now)
    {
        var lines = StripAnsi(output)
            .Replace("\r\n", "\n")
            .Split('\n', '\r')
            .Select(l => l.TrimEnd())
            .ToList();

        // Usage insights below this header contain percentages that are not quotas.
        var insights = lines.FindIndex(l => l.Contains("What's contributing", StringComparison.OrdinalIgnoreCase));
        if (insights >= 0)
        {
            lines = lines[..insights];
        }

        var windows = new List<UsageWindow>();
        for (var i = 0; i < lines.Count; i++)
        {
            var inline = InlineWindow().Match(lines[i]);
            if (inline.Success)
            {
                windows.Add(CreateWindow(inline.Groups["label"].Value, inline.Groups["pct"].Value, inline.Groups["kind"].Value,
                    inline.Groups["reset"].Success ? inline.Groups["reset"].Value : null, now));
                continue;
            }

            var header = HeaderLine().Match(lines[i]);
            if (!header.Success)
            {
                continue;
            }

            string? pct = null, kind = null, reset = null;
            for (var j = i + 1; j < Math.Min(lines.Count, i + 5); j++)
            {
                if (HeaderLine().IsMatch(lines[j]))
                {
                    break;
                }

                if (pct is null && PercentLine().Match(lines[j]) is { Success: true } p)
                {
                    pct = p.Groups["pct"].Value;
                    kind = p.Groups["kind"].Value;
                }
                else if (reset is null && ResetLine().IsMatch(lines[j]))
                {
                    reset = lines[j].Trim();
                }
            }

            if (pct is not null)
            {
                windows.Add(CreateWindow(header.Groups["label"].Value, pct, kind!, reset, now));
            }
        }

        if (windows.Count > 0)
        {
            return new UsageSnapshot(windows, now);
        }

        var text = string.Join('\n', lines);
        var marker = ErrorMarkers.FirstOrDefault(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
        var errorLine = marker is null
            ? Cli.CliProcess.FirstLine(text)
            : lines.First(l => l.Contains(marker, StringComparison.OrdinalIgnoreCase)).Trim();
        throw new UsageProviderException(errorLine is null
            ? "Claude printed an empty usage report."
            : $"Claude: {errorLine}");
    }

    private static UsageWindow CreateWindow(string rawLabel, string pct, string kind, string? reset, DateTimeOffset now)
    {
        var value = double.Parse(pct, CultureInfo.InvariantCulture);
        var used = kind.Equals("left", StringComparison.OrdinalIgnoreCase) ? 100 - value : value;
        var (group, label) = SplitLabel(rawLabel.Trim());
        var resetText = reset?.Trim();
        var resetsAt = resetText is null ? null : ClaudeResetParser.TryParse(resetText, now);
        return new UsageWindow(group, label, Math.Clamp(used, 0, 100), resetsAt, resetsAt is null ? resetText : null);
    }

    private static (string? Group, string Label) SplitLabel(string raw)
    {
        // "Current week (all models)" -> ("All models", "Weekly"); "Current session" -> (null, "Session").
        var match = Regex.Match(raw, @"^Current\s+(?<kind>\w+)\s*(?:\((?<scope>[^)]+)\))?", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return (null, raw);
        }

        var label = match.Groups["kind"].Value.ToLowerInvariant() switch
        {
            "session" => "Session",
            "week" => "Weekly",
            "day" => "Daily",
            var other => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(other),
        };
        var scope = match.Groups["scope"].Success ? match.Groups["scope"].Value.Trim() : null;
        if (scope is { Length: > 0 })
        {
            scope = char.ToUpperInvariant(scope[0]) + scope[1..];
        }

        return (scope, label);
    }
}

/// <summary>
/// Parses reset descriptions such as "resets Nov 5, 7pm (Asia/Tokyo)" or "Resets 11:30pm (Europe/Paris)".
/// </summary>
public static partial class ClaudeResetParser
{
    [GeneratedRegex(@"\((?<tz>[A-Za-z_]+(?:/[A-Za-z0-9_+\-]+)*)\)\s*$")]
    private static partial Regex TimeZoneSuffix();

    private static readonly string[] DateTimeFormats =
    [
        "MMM d h:mmtt", "MMM d htt", "MMM d yyyy h:mmtt", "MMM d yyyy htt",
        "MMMM d h:mmtt", "MMMM d htt", "MMM d", "MMMM d", "MMM d yyyy",
    ];

    private static readonly string[] TimeOnlyFormats = ["h:mmtt", "htt", "H:mm"];

    public static DateTimeOffset? TryParse(string text, DateTimeOffset now)
    {
        var value = Regex.Replace(text.Trim(), @"^resets?\s*", string.Empty, RegexOptions.IgnoreCase);
        var zone = TimeZoneInfo.Local;
        var tzMatch = TimeZoneSuffix().Match(value);
        if (tzMatch.Success)
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(tzMatch.Groups["tz"].Value, out var found))
            {
                zone = found;
            }

            value = value[..tzMatch.Index];
        }

        value = Regex.Replace(value, @"\s+at\s+|,", " ", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\s+", " ").Trim();
        value = Regex.Replace(value, @"(?<=\d)\s*(am|pm)\b", m => m.Groups[1].Value.ToUpperInvariant(), RegexOptions.IgnoreCase);
        if (value.Length == 0)
        {
            return null;
        }

        var nowInZone = TimeZoneInfo.ConvertTime(now, zone);
        const DateTimeStyles styles = DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.NoCurrentDateDefault;

        if (DateTime.TryParseExact(value, TimeOnlyFormats, CultureInfo.InvariantCulture, styles, out var timeOnly))
        {
            var candidate = nowInZone.Date + timeOnly.TimeOfDay;
            var result = ToOffset(candidate, zone);
            return result <= now ? ToOffset(candidate.AddDays(1), zone) : result;
        }

        if (DateTime.TryParseExact(value, DateTimeFormats, CultureInfo.InvariantCulture, styles, out var dateTime))
        {
            var hasYear = value.Split(' ').Any(part => part.Length == 4 && part.All(char.IsDigit));
            if (!hasYear)
            {
                dateTime = new DateTime(nowInZone.Year, dateTime.Month, dateTime.Day, dateTime.Hour, dateTime.Minute, 0);
                if (ToOffset(dateTime, zone) < now.AddDays(-1))
                {
                    dateTime = dateTime.AddYears(1);
                }
            }

            return ToOffset(dateTime, zone);
        }

        return null;
    }

    private static DateTimeOffset ToOffset(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }
}
