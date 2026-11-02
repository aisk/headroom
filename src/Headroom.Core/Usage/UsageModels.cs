namespace Headroom.Core.Usage;

/// <summary>
/// One quota window, e.g. "5-hour" or "Weekly".
/// </summary>
/// <param name="Group">Optional group the window belongs to (e.g. a model family).</param>
/// <param name="Label">Short window label.</param>
/// <param name="UsedPercent">Used share of the quota, 0..100.</param>
/// <param name="ResetsAt">When the window resets, if known.</param>
/// <param name="ResetDescription">Raw reset text reported by the CLI, used when no timestamp could be parsed.</param>
public sealed record UsageWindow(
    string? Group,
    string Label,
    double UsedPercent,
    DateTimeOffset? ResetsAt,
    string? ResetDescription = null)
{
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

public sealed record UsageSnapshot(
    IReadOnlyList<UsageWindow> Windows,
    DateTimeOffset FetchedAt,
    string? Account = null,
    string? Plan = null,
    string? Note = null)
{
    /// <summary>The window with the least remaining quota, used for compact summaries.</summary>
    public UsageWindow? MostConstrained => Windows.Count == 0 ? null : Windows.MinBy(w => w.RemainingPercent);
}

public enum UsageErrorKind
{
    General,

    /// <summary>The CLI is signed out or its credentials expired; the user has to sign in again.</summary>
    SignInRequired,
}

/// <summary>
/// A user-presentable failure while reading usage from a CLI.
/// </summary>
public class UsageProviderException(string message, Exception? inner = null, UsageErrorKind kind = UsageErrorKind.General)
    : Exception(message, inner)
{
    public UsageProviderException(string message, UsageErrorKind kind)
        : this(message, null, kind)
    {
    }

    public UsageErrorKind Kind { get; } = kind;
}

public interface IUsageProvider
{
    string Id { get; }

    string DisplayName { get; }

    /// <summary>
    /// When signed out, stop automatic refreshes until the user refreshes manually. Used for CLIs
    /// whose usage probe may start an interactive (browser) sign-in when no login is available.
    /// </summary>
    bool PauseAutoRefreshWhenSignedOut => false;

    /// <summary>Whether the backing CLI can be found on this machine.</summary>
    bool IsInstalled();

    Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken);
}
