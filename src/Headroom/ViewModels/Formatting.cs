using System.Globalization;

namespace Headroom.ViewModels;

internal static class Formatting
{
    public static string Relative(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalMinutes < 1)
        {
            return "<1m";
        }

        if (span.TotalHours < 1)
        {
            return $"{(int)span.TotalMinutes}m";
        }

        if (span.TotalDays < 1)
        {
            return span.Minutes == 0 ? $"{(int)span.TotalHours}h" : $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        return span.Hours == 0 ? $"{(int)span.TotalDays}d" : $"{(int)span.TotalDays}d {span.Hours}h";
    }

    public static string ResetText(DateTimeOffset? resetsAt, string? description, DateTimeOffset now)
    {
        if (resetsAt is { } at)
        {
            var local = at.ToLocalTime();
            var when = local.Date == now.LocalDateTime.Date
                ? local.ToString("t", CultureInfo.CurrentCulture)
                : $"{local.ToString("MMM d", CultureInfo.CurrentCulture)} {local.ToString("t", CultureInfo.CurrentCulture)}";
            return at <= now ? $"Reset due · {when}" : $"Resets in {Relative(at - now)} · {when}";
        }

        return description ?? string.Empty;
    }

    public static string Updated(DateTimeOffset? at, DateTimeOffset now) =>
        at is { } value ? $"Updated {Relative(now - value)} ago".Replace("<1m ago", "just now") : "Not updated yet";
}
