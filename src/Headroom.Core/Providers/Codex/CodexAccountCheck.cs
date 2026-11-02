using System.Text.Json.Nodes;
using Headroom.Core.Usage;

namespace Headroom.Core.Providers.Codex;

/// <summary>Interprets the <c>account/read</c> result before rate limits are requested.</summary>
public static class CodexAccountCheck
{
    private static readonly string[] AuthenticationMarkers =
    [
        "unauthorized", "token expired", "token_expired", "invalid token", "refresh token",
        "not logged in", "not signed in", "codex login",
    ];

    public static void ThrowIfUnusable(JsonNode? accountResult)
    {
        if (accountResult is not JsonObject result)
        {
            return; // account/read is optional; let the rate limit request decide.
        }

        var account = result["account"] as JsonObject;
        if (account is null)
        {
            if (result["requiresOpenaiAuth"] is JsonValue requires && requires.TryGetValue<bool>(out var value) && value)
            {
                throw new UsageProviderException("Codex is not signed in. Run `codex login`.", UsageErrorKind.SignInRequired);
            }

            return;
        }

        var type = account["type"] is JsonValue t && t.TryGetValue<string>(out var text) ? text : null;
        if (string.Equals(type, "apiKey", StringComparison.OrdinalIgnoreCase))
        {
            throw new UsageProviderException(
                "Codex is signed in with an API key; ChatGPT plan limits are only available after `codex login` with ChatGPT.");
        }
    }

    public static bool IsAuthenticationFailure(string message) =>
        System.Text.RegularExpressions.Regex.IsMatch(message, @"\b401\b") ||
        AuthenticationMarkers.Any(m => message.Contains(m, StringComparison.OrdinalIgnoreCase));
}
