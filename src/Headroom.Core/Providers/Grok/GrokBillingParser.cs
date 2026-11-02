using System.Globalization;
using System.Text.Json.Nodes;
using Headroom.Core.Usage;

namespace Headroom.Core.Providers.Grok;

/// <summary>
/// Parses the result of the <c>_x.ai/billing</c> ACP extension of <c>grok agent stdio</c>.
/// Monetary amounts are reported as <c>{ "val": cents }</c>.
/// </summary>
public static class GrokBillingParser
{
    public static UsageSnapshot Parse(JsonNode? result, DateTimeOffset now)
    {
        if (result is not JsonObject billing || billing["config"] is not JsonObject config)
        {
            throw new UsageProviderException("Grok returned no billing data.");
        }

        var period = config["currentPeriod"] as JsonObject;
        var start = ReadDate(period?["start"]) ?? ReadDate(config["billingPeriodStart"]);
        var end = ReadDate(period?["end"]) ?? ReadDate(config["billingPeriodEnd"]);
        var label = PeriodLabel(period?["type"]?.ToString(), start, end);

        var windows = new List<UsageWindow>();
        var used = ReadDouble(config["creditUsagePercent"]);
        if (used is null)
        {
            var cap = Cents(config["onDemandCap"]);
            var spent = Cents(config["onDemandUsed"]);
            if (cap is > 0 && spent is not null)
            {
                used = spent.Value / cap.Value * 100;
            }
        }

        if (used is { } percent)
        {
            windows.Add(new UsageWindow(null, label, Math.Clamp(percent, 0, 100), end));
        }

        var notes = new List<string>();
        if (Cents(config["prepaidBalance"]) is > 0 and var balance)
        {
            notes.Add($"Prepaid balance: {(balance / 100).ToString("C", CultureInfo.GetCultureInfo("en-US"))}");
        }

        if (windows.Count == 0)
        {
            // Some plans (e.g. Free) publish only the billing period. Never invent a percentage.
            notes.Add(end is { } reset
                ? $"Grok does not report a usage percentage for this plan. {label} period ends {reset.ToLocalTime():MMM d HH:mm}."
                : "Grok does not report a usage percentage for this plan.");
        }

        return new UsageSnapshot(
            windows,
            now,
            Plan: billing["subscription_tier"]?.ToString(),
            Note: notes.Count == 0 ? null : string.Join(' ', notes));
    }

    private static string PeriodLabel(string? type, DateTimeOffset? start, DateTimeOffset? end)
    {
        if (type is not null)
        {
            if (type.Contains("WEEK", StringComparison.OrdinalIgnoreCase))
            {
                return "Weekly";
            }

            if (type.Contains("MONTH", StringComparison.OrdinalIgnoreCase))
            {
                return "Monthly";
            }

            if (type.Contains("DAY", StringComparison.OrdinalIgnoreCase) || type.Contains("DAILY", StringComparison.OrdinalIgnoreCase))
            {
                return "Daily";
            }
        }

        if (start is { } s && end is { } e)
        {
            var days = (e - s).TotalDays;
            return days switch
            {
                >= 6.5 and <= 7.5 => "Weekly",
                >= 27 and <= 32 => "Monthly",
                >= 0.9 and <= 1.1 => "Daily",
                _ => "Credits",
            };
        }

        return "Credits";
    }

    private static double? Cents(JsonNode? node) => ReadDouble(node is JsonObject o ? o["val"] : node);

    private static double? ReadDouble(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<double>(out var number))
        {
            return number;
        }

        // Some int64 fields are serialized as strings.
        return value.TryGetValue<string>(out var text) &&
               double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static DateTimeOffset? ReadDate(JsonNode? node) =>
        node?.ToString() is { } text &&
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;
}
