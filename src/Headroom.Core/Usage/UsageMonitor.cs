namespace Headroom.Core.Usage;

public sealed class ProviderState(IUsageProvider provider)
{
    public IUsageProvider Provider { get; } = provider;

    public UsageSnapshot? Snapshot { get; internal set; }

    public string? Error { get; internal set; }

    /// <summary>The last refresh found the CLI signed out or its credentials expired.</summary>
    public bool NeedsSignIn { get; internal set; }

    /// <summary>Automatic refreshes are suspended until the user refreshes manually.</summary>
    public bool IsAutoRefreshPaused => NeedsSignIn && Provider.PauseAutoRefreshWhenSignedOut;

    public bool IsRefreshing { get; internal set; }

    public bool IsInstalled { get; internal set; } = true;

    /// <summary>Turned off by the user: never polled and not shown.</summary>
    public bool IsEnabled { get; internal set; } = true;

    public DateTimeOffset? LastAttempt { get; internal set; }
}

/// <summary>
/// Periodically refreshes every enabled provider. Refreshes of the same provider never overlap.
/// </summary>
public sealed class UsageMonitor : IDisposable
{
    private readonly Dictionary<ProviderState, SemaphoreSlim> _gates = [];
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _loop;

    public UsageMonitor(IEnumerable<IUsageProvider> providers)
    {
        Providers = providers.Select(p => new ProviderState(p)).ToList();
        foreach (var state in Providers)
        {
            _gates[state] = new SemaphoreSlim(1, 1);
        }
    }

    public IReadOnlyList<ProviderState> Providers { get; }

    /// <summary>Raised on a thread-pool thread whenever a provider state changes.</summary>
    public event Action<ProviderState>? StateChanged;

    /// <summary>Time between automatic refreshes, or null when only manual refreshes run.</summary>
    public TimeSpan? Interval { get; private set; }

    /// <summary>
    /// Starts automatic refreshes, or changes their interval. The first round refreshes providers
    /// not attempted within the new interval; null stops automatic refreshes.
    /// </summary>
    public void SetInterval(TimeSpan? interval)
    {
        _loop?.Cancel();
        _loop?.Dispose();
        _loop = null;
        Interval = interval;
        if (interval is not { } period || _lifetime.IsCancellationRequested)
        {
            return;
        }

        _loop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = RunLoopAsync(period, _loop.Token);
    }

    /// <summary>
    /// Turns a provider on or off. A provider turned on is refreshed if its data is stale; the
    /// returned task completes when that refresh does.
    /// </summary>
    public Task SetEnabled(ProviderState state, bool enabled)
    {
        if (state.IsEnabled == enabled)
        {
            return Task.CompletedTask;
        }

        state.IsEnabled = enabled;
        StateChanged?.Invoke(state);
        return enabled ? RefreshAsync(state, Interval) : Task.CompletedTask;
    }

    /// <param name="maxAge">Skip providers attempted more recently than this.</param>
    /// <param name="manual">A user-initiated refresh; also retries providers whose auto-refresh is paused.</param>
    public Task RefreshAllAsync(TimeSpan? maxAge = null, bool manual = false) =>
        Task.WhenAll(Providers.Select(p => RefreshAsync(p, maxAge, manual)));

    public async Task RefreshAsync(ProviderState state, TimeSpan? maxAge = null, bool manual = false)
    {
        if (!state.IsEnabled)
        {
            return;
        }

        if (!manual && state.IsAutoRefreshPaused)
        {
            return;
        }

        if (maxAge is { } age && state.LastAttempt is { } last && DateTimeOffset.Now - last < age)
        {
            return;
        }

        var gate = _gates[state];
        if (!await gate.WaitAsync(0).ConfigureAwait(false))
        {
            return; // Already refreshing.
        }

        try
        {
            state.IsInstalled = state.Provider.IsInstalled();
            if (!state.IsInstalled)
            {
                state.Error = null;
                state.NeedsSignIn = false;
                state.Snapshot = null;
                state.LastAttempt = DateTimeOffset.Now;
                StateChanged?.Invoke(state);
                return;
            }

            state.IsRefreshing = true;
            StateChanged?.Invoke(state);
            try
            {
                state.Snapshot = await Task.Run(() => state.Provider.FetchAsync(_lifetime.Token)).ConfigureAwait(false);
                state.Error = null;
                state.NeedsSignIn = false;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (UsageProviderException ex)
            {
                state.Error = ex.Message;
                state.NeedsSignIn = ex.Kind == UsageErrorKind.SignInRequired;
                if (state.NeedsSignIn)
                {
                    // The account may have changed or signed out; never keep showing its old quota.
                    state.Snapshot = null;
                }
            }
            catch (Exception ex)
            {
                state.Error = $"Unexpected error: {ex.Message}";
                state.NeedsSignIn = false;
            }

            state.LastAttempt = DateTimeOffset.Now;
            state.IsRefreshing = false;
            StateChanged?.Invoke(state);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RunLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            // After an interval change, data that is still fresh under the new interval is kept.
            await RefreshAllAsync(maxAge: interval).ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await RefreshAllAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Interval changed or the monitor was disposed.
        }
    }

    public void Dispose()
    {
        _loop?.Cancel();
        _loop?.Dispose();
        _loop = null;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
