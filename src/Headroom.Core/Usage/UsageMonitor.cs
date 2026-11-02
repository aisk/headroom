namespace Headroom.Core.Usage;

public sealed class ProviderState(IUsageProvider provider)
{
    public IUsageProvider Provider { get; } = provider;

    public UsageSnapshot? Snapshot { get; internal set; }

    public string? Error { get; internal set; }

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

    public Task RefreshAllAsync(TimeSpan? maxAge = null) =>
        Task.WhenAll(Providers.Select(p => RefreshAsync(p, maxAge)));

    public async Task RefreshAsync(ProviderState state, TimeSpan? maxAge = null)
    {
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
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (UsageProviderException ex)
            {
                state.Error = ex.Message;
            }
            catch (Exception ex)
            {
                state.Error = $"Unexpected error: {ex.Message}";
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
