using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Headroom.Core.Usage;

namespace Headroom.Core.Providers.Antigravity;

/// <summary>
/// Parses the report printed by <c>agy -p /usage --output-format json</c>.
/// </summary>
public static class AntigravityUsageParser
{
    /// <param name="exitCode">Exit code of agy, used to classify failed reports.</param>
    /// <param name="stderr">Standard error of agy, used only for classification and never shown.</param>
    public static UsageSnapshot Parse(string json, DateTimeOffset now, int exitCode = 0, string? stderr = null)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new UsageProviderException("agy returned an unreadable usage report.", ex);
        }

        if (root is not JsonObject report)
        {
            throw new UsageProviderException("agy returned an empty usage report.");
        }

        var status = report["status"]?.GetValue<string>();
        if (!string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            // The detail can contain account information, so it is only used for classification.
            var detail = $"{report["error"]}\n{report["response"]}\n{stderr}";
            throw AntigravityFailureClassifier.TryClassify(exitCode, detail)
                  ?? new UsageProviderException($"agy usage report failed ({status ?? "unknown status"}).");
        }

        var command = report["command"] as JsonObject;
        if (!string.Equals(command?["name"]?.GetValue<string>(), "usage", StringComparison.Ordinal) ||
            command?["data"]?["groups"] is not JsonArray groups)
        {
            throw new UsageProviderException("agy did not return a usage report.");
        }

        var windows = new List<UsageWindow>();
        foreach (var group in groups.OfType<JsonObject>())
        {
            var groupName = group["name"]?.GetValue<string>();
            if (group["buckets"] is not JsonArray buckets)
            {
                continue;
            }

            foreach (var bucket in buckets.OfType<JsonObject>())
            {
                if (bucket["remaining_fraction"] is not JsonValue fractionNode ||
                    !fractionNode.TryGetValue<double>(out var remaining))
                {
                    continue;
                }

                var used = Math.Clamp((1 - remaining) * 100, 0, 100);
                var label = WindowLabel(bucket["window"]?.GetValue<string>(), bucket["name"]?.GetValue<string>());
                DateTimeOffset? resetsAt = null;
                var resetText = bucket["reset_time"]?.GetValue<string>();
                if (resetText is not null &&
                    DateTimeOffset.TryParse(resetText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                {
                    resetsAt = parsed;
                }

                windows.Add(new UsageWindow(groupName, label, used, resetsAt, resetsAt is null ? resetText : null));
            }
        }

        if (windows.Count == 0)
        {
            throw new UsageProviderException("agy reported no quota buckets for this account.");
        }

        return new UsageSnapshot(windows, now);
    }

    private static string WindowLabel(string? window, string? name) => window?.ToLowerInvariant() switch
    {
        "weekly" => "Weekly",
        "5h" => "5-hour",
        "daily" => "Daily",
        _ => name ?? window ?? "Limit",
    };
}
