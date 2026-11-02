using System.Collections.ObjectModel;
using Headroom.Core.Usage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Headroom.ViewModels;

public sealed class UsageWindowViewModel(UsageWindow window) : ObservableObject
{
    private string _resetText = string.Empty;

    public UsageWindow Window { get; } = window;

    public string Title => Window.Group is null ? Window.Label : $"{Window.Group} · {Window.Label}";

    public double Remaining => Window.RemainingPercent;

    public string RemainingText => $"{Math.Round(Window.RemainingPercent):0}% left";

    public Brush BarBrush => (Brush)Application.Current.Resources[Window.RemainingPercent switch
    {
        <= 10 => "SystemFillColorCriticalBrush",
        <= 25 => "SystemFillColorCautionBrush",
        _ => "AccentFillColorDefaultBrush",
    }];

    public string ResetText
    {
        get => _resetText;
        private set => SetProperty(ref _resetText, value);
    }

    public void RefreshTexts(DateTimeOffset now) =>
        ResetText = Formatting.ResetText(Window.ResetsAt, Window.ResetDescription, now);
}

public sealed class ProviderViewModel(ProviderState state) : ObservableObject
{
    private string _subtitle = string.Empty;
    private string? _error;
    private string? _note;
    private bool _isRefreshing;
    private bool _isVisible = true;
    private UsageSnapshot? _shownSnapshot;

    public ProviderState State { get; } = state;

    public string Name => State.Provider.DisplayName;

    public ObservableCollection<UsageWindowViewModel> Windows { get; } = [];

    public bool HasWindows => Windows.Count > 0;

    public string Subtitle
    {
        get => _subtitle;
        private set => SetProperty(ref _subtitle, value);
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                RaisePropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    public string? Note
    {
        get => _note;
        private set
        {
            if (SetProperty(ref _note, value))
            {
                RaisePropertyChanged(nameof(HasNote));
            }
        }
    }

    public bool HasNote => !string.IsNullOrEmpty(_note);

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set => SetProperty(ref _isRefreshing, value);
    }

    /// <summary>Providers whose CLI is not installed are hidden.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        private set => SetProperty(ref _isVisible, value);
    }

    /// <summary>Remaining percent of the most constrained window, for the tray icon.</summary>
    public double? Remaining => State.Snapshot?.MostConstrained?.RemainingPercent;

    public void Update(DateTimeOffset now)
    {
        IsVisible = State.IsInstalled;
        IsRefreshing = State.IsRefreshing;
        Error = State.Error;
        Note = State.Snapshot?.Note;

        var snapshot = State.Snapshot;
        if (!ReferenceEquals(snapshot, _shownSnapshot))
        {
            _shownSnapshot = snapshot;
            Windows.Clear();
            foreach (var window in snapshot?.Windows ?? [])
            {
                Windows.Add(new UsageWindowViewModel(window));
            }

            RaisePropertyChanged(nameof(HasWindows));
        }

        RefreshTexts(now);
    }

    public void RefreshTexts(DateTimeOffset now)
    {
        var snapshot = State.Snapshot;
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(snapshot?.Plan))
        {
            parts.Add(snapshot.Plan);
        }

        if (!string.IsNullOrEmpty(snapshot?.Account))
        {
            parts.Add(snapshot.Account);
        }

        if (State.IsRefreshing)
        {
            parts.Add("Refreshing…");
        }
        else if (snapshot is not null)
        {
            parts.Add(Formatting.Updated(snapshot.FetchedAt, now));
        }

        Subtitle = string.Join(" · ", parts);
        foreach (var window in Windows)
        {
            window.RefreshTexts(now);
        }
    }
}
