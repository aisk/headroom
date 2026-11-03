using Headroom.Core.Settings;
using Headroom.Core.Usage;

namespace Headroom.Core.Tests;

public class AppSettingsTests
{
    [Fact]
    public void RoundTripsThroughJson()
    {
        var settings = new AppSettings { RefreshIntervalMinutes = 15, RefreshOnOpen = false };
        settings.SetProviderEnabled("grok", false);

        var loaded = AppSettings.Parse(settings.ToJson());

        Assert.Equal(15, loaded.RefreshIntervalMinutes);
        Assert.Equal(TimeSpan.FromMinutes(15), loaded.RefreshInterval);
        Assert.False(loaded.RefreshOnOpen);
        Assert.False(loaded.IsProviderEnabled("Grok"));
        Assert.True(loaded.IsProviderEnabled("claude"));
    }

    [Fact]
    public void MissingAndInvalidValuesUseDefaults()
    {
        var settings = AppSettings.Parse("""{"refreshIntervalMinutes": -3, "disabledProviders": null}""");

        Assert.Equal(AppSettings.DefaultRefreshIntervalMinutes, settings.RefreshIntervalMinutes);
        Assert.True(settings.RefreshOnOpen);
        Assert.Empty(settings.DisabledProviders);
    }

    [Fact]
    public void ZeroIntervalMeansManualOnly() =>
        Assert.Null(new AppSettings { RefreshIntervalMinutes = 0 }.RefreshInterval);

    [Fact]
    public void ReenablingRemovesEveryEntry()
    {
        var settings = new AppSettings { DisabledProviders = ["codex", "Codex"] };
        settings.SetProviderEnabled("codex", true);
        Assert.Empty(settings.DisabledProviders);
    }

    [Fact]
    public void StoreFallsBackToDefaultsForCorruptFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"headroom-{Guid.NewGuid():N}", "settings.json");
        var store = new SettingsStore(path);
        try
        {
            Assert.Equal(AppSettings.DefaultRefreshIntervalMinutes, store.Load().RefreshIntervalMinutes);

            Assert.True(store.Save(new AppSettings { RefreshIntervalMinutes = 30 }));
            Assert.Equal(30, store.Load().RefreshIntervalMinutes);

            File.WriteAllText(path, "{ not json");
            Assert.Equal(AppSettings.DefaultRefreshIntervalMinutes, store.Load().RefreshIntervalMinutes);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}

public class UsageMonitorEnabledTests
{
    private sealed class CountingProvider : IUsageProvider
    {
        public int Calls { get; private set; }

        public string Id => "test";

        public string DisplayName => "Test";

        public bool IsInstalled() => true;

        public Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new UsageSnapshot([], DateTimeOffset.Now));
        }
    }

    [Fact]
    public async Task DisabledProviderIsNeverPolled()
    {
        var provider = new CountingProvider();
        using var monitor = new UsageMonitor([provider]);
        await monitor.SetEnabled(monitor.Providers[0], false);

        await monitor.RefreshAllAsync();
        await monitor.RefreshAllAsync(manual: true);
        Assert.Equal(0, provider.Calls);

        await monitor.SetEnabled(monitor.Providers[0], true);
        Assert.Equal(1, provider.Calls);
    }
}
