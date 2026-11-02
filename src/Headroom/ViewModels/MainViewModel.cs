using System.Collections.ObjectModel;
using Headroom.Core.Usage;
using Microsoft.UI.Dispatching;

namespace Headroom.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly UsageMonitor _monitor;
    private readonly DispatcherQueue _dispatcher;
    private bool _hasVisibleProviders = true;

    public MainViewModel(UsageMonitor monitor, DispatcherQueue dispatcher)
    {
        _monitor = monitor;
        _dispatcher = dispatcher;
        foreach (var state in monitor.Providers)
        {
            Providers.Add(new ProviderViewModel(state));
        }

        monitor.StateChanged += state => _dispatcher.TryEnqueue(() =>
        {
            var now = DateTimeOffset.Now;
            Providers.First(p => p.State == state).Update(now);
            HasVisibleProviders = Providers.Any(p => p.IsVisible);
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    public ObservableCollection<ProviderViewModel> Providers { get; } = [];

    /// <summary>Raised on the UI thread after any provider changes.</summary>
    public event EventHandler? Changed;

    public bool HasVisibleProviders
    {
        get => _hasVisibleProviders;
        private set
        {
            if (SetProperty(ref _hasVisibleProviders, value))
            {
                RaisePropertyChanged(nameof(HasNoProviders));
            }
        }
    }

    public bool HasNoProviders => !_hasVisibleProviders;

    public void RefreshTexts()
    {
        var now = DateTimeOffset.Now;
        foreach (var provider in Providers)
        {
            provider.RefreshTexts(now);
        }
    }

    public Task RefreshAsync(TimeSpan? maxAge = null) => _monitor.RefreshAllAsync(maxAge);

    public IReadOnlyList<double?> TrayBars() =>
        Providers.Where(p => p.IsVisible).Select(p => p.Remaining).ToList();

    public string TrayTooltip()
    {
        var lines = new List<string> { "Headroom" };
        foreach (var provider in Providers.Where(p => p.IsVisible))
        {
            var constrained = provider.State.Snapshot?.MostConstrained;
            lines.Add(constrained is not null
                ? $"{provider.Name}: {Math.Round(constrained.RemainingPercent):0}% left ({constrained.Label})"
                : provider.HasError ? $"{provider.Name}: error" : $"{provider.Name}: …");
        }

        return string.Join('\n', lines);
    }
}
