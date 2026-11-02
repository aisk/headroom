using System.Text.Json.Nodes;
using Headroom.Core.Usage;

namespace Headroom.Core.Providers.Codex;

/// <summary>
/// Maps the <c>account/rateLimits/read</c> and <c>account/read</c> results of <c>codex app-server</c>.
/// </summary>
public static class CodexRateLimitParser
{
    private const string DefaultLimitId = "codex";

    public static UsageSnapshot Parse(JsonNode? rateLimitsResult, JsonNode? accountResult, DateTimeOffset now)
    {
        if (rateLimitsResult is not JsonObject result)
        {
            throw new UsageProviderException("Codex returned no rate limit data.");
        }

        var windows = new List<UsageWindow>();
        var primaryLimit = result["rateLimits"] as JsonObject;
        var primaryLimitId = primaryLimit?["limitId"]?.GetValue<string>() ?? DefaultLimitId;
        if (primaryLimit is not null)
        {
            AddWindows(windows, primaryLimit, group: null);
        }

        // Additional limits (e.g. model-specific pools) are grouped under their name.
        if (result["rateLimitsByLimitId"] is JsonObject byId)
        {
            foreach (var (limitId, node) in byId)
            {
                if (limitId == primaryLimitId || node is not JsonObject limit)
                {
                    continue;
                }

                AddWindows(windows, limit, limit["limitName"]?.GetValue<string>() ?? limitId);
            }
        }

        if (windows.Count == 0)
        {
            throw new UsageProviderException("Codex reported no usage windows for this account.");
        }

        var account = accountResult?["account"] as JsonObject;
        var plan = primaryLimit?["planType"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(plan))
        {
            plan = account?["planType"]?.GetValue<string>();
        }

        return new UsageSnapshot(
            windows,
            now,
            Account: account?["email"]?.GetValue<string>(),
            Plan: FormatPlan(plan),
            Note: primaryLimit is null ? null : DescribeCredits(primaryLimit));
    }

    private static void AddWindows(List<UsageWindow> windows, JsonObject limit, string? group)
    {
        foreach (var key in (ReadOnlySpan<string>)["primary", "secondary"])
        {
            if (limit[key] is not JsonObject window ||
                window["usedPercent"] is not JsonValue usedNode ||
                !usedNode.TryGetValue<double>(out var used))
            {
                continue;
            }

            int? minutes = window["windowDurationMins"] is JsonValue m && m.TryGetValue<int>(out var mins) ? mins : null;
            DateTimeOffset? resetsAt = window["resetsAt"] is JsonValue r && r.TryGetValue<long>(out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix)
                : null;
            windows.Add(new UsageWindow(group, WindowLabel(minutes, key), Math.Clamp(used, 0, 100), resetsAt));
        }
    }

    internal static string WindowLabel(int? minutes, string fallbackKey) => minutes switch
    {
        null => fallbackKey == "primary" ? "Session" : "Weekly",
        10080 => "Weekly",
        1440 => "Daily",
        _ when minutes % 1440 == 0 => $"{minutes / 1440}-day",
        _ when minutes % 60 == 0 => $"{minutes / 60}-hour",
        _ => $"{minutes}-minute",
    };

    private static string? DescribeCredits(JsonObject limit)
    {
        if (limit["credits"] is not JsonObject credits)
        {
            return null;
        }

        if (credits["unlimited"]?.GetValue<bool>() == true)
        {
            return "Credits: unlimited";
        }

        if (credits["hasCredits"]?.GetValue<bool>() == true)
        {
            return $"Credits: {credits["balance"]}";
        }

        return null;
    }

    private static string? FormatPlan(string? plan) =>
        string.IsNullOrWhiteSpace(plan) ? null : char.ToUpperInvariant(plan[0]) + plan[1..];
}
