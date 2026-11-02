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

    public DateTimeOffset? LastAttempt { get; internal set; }
}

/// <summary>
/// Periodically refreshes every provider. Refreshes of the same provider never overlap.
/// </summary>
public sealed class UsageMonitor : IDisposable
{
    private readonly Dictionary<ProviderState, SemaphoreSlim> _gates = [];
    private readonly CancellationTokenSource _lifetime = new();

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

    public void Start(TimeSpan interval)
    {
        _ = RunLoopAsync(interval, _lifetime.Token);
    }

    /// <param name="maxAge">Skip providers attempted more recently than this.</param>
    /// <param name="manual">A user-initiated refresh; also retries providers whose auto-refresh is paused.</param>
    public Task RefreshAllAsync(TimeSpan? maxAge = null, bool manual = false) =>
        Task.WhenAll(Providers.Select(p => RefreshAsync(p, maxAge, manual)));

    public async Task RefreshAsync(ProviderState state, TimeSpan? maxAge = null, bool manual = false)
    {
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
        do
        {
            await RefreshAllAsync().ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
