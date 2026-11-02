using System.Text.Json;
using System.Text.Json.Nodes;

namespace Headroom.Core.Providers.Claude;

/// <summary>
/// Parsed output of <c>claude auth status --json</c>, which reads the local login without contacting the model.
/// </summary>
public sealed record ClaudeAuthStatus(
    bool LoggedIn,
    string? AuthMethod,
    string? ApiProvider,
    string? Email,
    string? SubscriptionType)
{
    /// <summary>Signed in with a claude.ai subscription, the only login that has /usage quotas.</summary>
    public bool IsSubscription =>
        string.Equals(AuthMethod, "claude.ai", StringComparison.OrdinalIgnoreCase) &&
        (ApiProvider is null || string.Equals(ApiProvider, "firstParty", StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns null when the output is not the expected JSON (e.g. an older CLI).</summary>
    public static ClaudeAuthStatus? TryParse(string output)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(output[start..(end + 1)]) is not JsonObject json ||
                json["loggedIn"] is not JsonValue loggedInNode ||
                !loggedInNode.TryGetValue<bool>(out var loggedIn))
            {
                return null;
            }

            return new ClaudeAuthStatus(
                loggedIn,
                ReadString(json, "authMethod"),
                ReadString(json, "apiProvider"),
                ReadString(json, "email"),
                ReadString(json, "subscriptionType"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string? PlanDisplayName => SubscriptionType switch
    {
        null or "" => null,
        var value => char.ToUpperInvariant(value[0]) + value[1..],
    };

    private static string? ReadString(JsonObject json, string key) =>
        json[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
}
