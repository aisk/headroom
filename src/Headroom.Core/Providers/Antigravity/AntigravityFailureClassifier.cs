using System.Text.RegularExpressions;
using Headroom.Core.Usage;

namespace Headroom.Core.Providers.Antigravity;

/// <summary>
/// Maps a failed <c>agy</c> usage report to a fixed message. Raw CLI output is never shown because it can
/// contain account-identifying URLs, proxy details, or local paths. Markers follow CodexBar's classifier.
/// </summary>
public static class AntigravityFailureClassifier
{
    // A blocking login prompt or an explicit logout. A transient "not signed in" line alone is not
    // enough evidence because agy prints it briefly before silently refreshing an existing login.
    private static readonly Regex[] AuthenticationPrompts =
    [
        new(@"select\s+login\s+method\s*:?", RegexOptions.IgnoreCase),
        new(@"you\s+are\s+not\s+logged\s+into\s+antigravity", RegexOptions.IgnoreCase),
        new(@"keyring\s*auth\s*:\s*timed\s+out\b", RegexOptions.IgnoreCase),
    ];

    private static readonly string[] SignInMarkers =
    [
        "not logged in", "not signed in", "unauthenticated", "authentication required", "login required",
        "please log in", "please sign in", "sign in required", "sign-in required",
    ];

    private static readonly string[] EligibilityMarkers =
    [
        "eligibility check failed", "not eligible", "does not support google tos", "unsupported country", "unsupported region",
    ];

    private static readonly Regex[] NetworkMarkers =
    [
        .. new[]
        {
            @"\beof\b", @"\btimed out\b", @"\btimeout\b", @"\bdeadline exceeded\b", @"\bi/o timeout\b",
            @"\bno such host\b", @"\bdns\b", @"\bconnection refused\b", @"\bconnection reset\b",
            @"\bnetwork is unreachable\b", @"\bunreachable\b", @"\bproxy\b", @"\bcertificate\b", @"\btls\b",
            @"\bssl\b", @"get ""http", @"post ""http",
        }.Select(p => new Regex(p, RegexOptions.IgnoreCase)),
    ];

    public static UsageProviderException Classify(int exitCode, string output) =>
        TryClassify(exitCode, output) ?? new UsageProviderException($"agy exited {exitCode} without a usage report.");

    /// <summary>Returns null when the output matches no known failure.</summary>
    public static UsageProviderException? TryClassify(int exitCode, string output)
    {
        if (AuthenticationPrompts.Any(r => r.IsMatch(output)) ||
            SignInMarkers.Any(m => output.Contains(m, StringComparison.OrdinalIgnoreCase)))
        {
            return new UsageProviderException(
                "Antigravity is not signed in. Run `agy` in a terminal to sign in, then refresh.",
                UsageErrorKind.SignInRequired);
        }

        var networkFailed = NetworkMarkers.Any(r => r.IsMatch(output));
        if (EligibilityMarkers.Any(m => output.Contains(m, StringComparison.OrdinalIgnoreCase)))
        {
            return new UsageProviderException(networkFailed
                ? $"agy exited {exitCode}; the eligibility check failed on a network request (check network or proxy settings)."
                : $"agy exited {exitCode}; the account is not eligible for Antigravity.");
        }

        if (networkFailed)
        {
            return new UsageProviderException(
                $"agy exited {exitCode}; a network request failed (check network or proxy settings).");
        }

        return null;
    }
}
