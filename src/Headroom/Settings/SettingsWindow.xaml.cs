using Headroom.Core.Settings;
using Headroom.Core.Usage;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;
using static Headroom.Interop.NativeMethods;

namespace Headroom.Settings;

/// <summary>
/// Choose whether Headroom starts with Windows, which tools are shown and how often usage refreshes.
/// Changes apply immediately.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private const int WidthDip = 440;
    private const int HeightDip = 560;

    private readonly AppSettings _settings;
    private readonly IntPtr _hwnd;
    private bool _loading = true;

    public SettingsWindow(AppSettings settings, IReadOnlyList<ProviderState> providers)
    {
        _settings = settings;
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        AppWindow.SetIcon("Assets/AppIcon.ico");
        var scale = GetDpiForWindow(_hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32((int)Math.Round(WidthDip * scale), (int)Math.Round(HeightDip * scale)));
        CenterOnScreen();

        foreach (var state in providers)
        {
            var provider = state.Provider;
            var box = new CheckBox
            {
                Content = provider.DisplayName,
                IsChecked = settings.IsProviderEnabled(provider.Id),
            };
            box.Click += (_, _) =>
            {
                _settings.SetProviderEnabled(provider.Id, box.IsChecked == true);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            };
            ProviderList.Children.Add(box);
        }

        var choices = AppSettings.RefreshIntervalChoices.ToList();
        if (!choices.Contains(settings.RefreshIntervalMinutes))
        {
            // Keep a value edited by hand in the settings file selectable.
            choices.Add(settings.RefreshIntervalMinutes);
            choices.Sort();
        }

        foreach (var minutes in choices)
        {
            IntervalBox.Items.Add(new ComboBoxItem { Content = IntervalText(minutes), Tag = minutes });
        }

        IntervalBox.SelectedIndex = choices.IndexOf(settings.RefreshIntervalMinutes);
        RefreshOnOpenBox.IsChecked = settings.RefreshOnOpen;
        _loading = false;

        // The startup state can also change in Windows Settings or Task Manager, so re-read it whenever the
        // window comes back to the front.
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
            {
                _ = LoadAutoStartAsync();
            }
        };
    }

    /// <summary>Raised after the user changed a setting; the settings object is already updated.</summary>
    public event EventHandler? SettingsChanged;

    public void BringToFront()
    {
        Activate();
        SetForegroundWindow(_hwnd);
    }

    private static string IntervalText(int minutes) => minutes switch
    {
        0 => "Off (manual only)",
        1 => "Every minute",
        60 => "Every hour",
        _ when minutes % 60 == 0 => $"Every {minutes / 60} hours",
        _ => $"Every {minutes} minutes",
    };

    private void CenterOnScreen()
    {
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        var size = AppWindow.Size;
        AppWindow.Move(new PointInt32(work.X + (work.Width - size.Width) / 2, work.Y + (work.Height - size.Height) / 2));
    }

    private async Task LoadAutoStartAsync()
    {
        try
        {
            ShowAutoStart(await AutoStart.GetStateAsync());
        }
        catch (Exception)
        {
            ShowAutoStart(null);
        }
    }

    /// <summary>Shows the auto start state; null means it could not be read.</summary>
    private void ShowAutoStart(AutoStartState? state)
    {
        AutoStartBox.IsChecked = state is AutoStartState.Enabled;
        AutoStartBox.IsEnabled = state is AutoStartState.Enabled or AutoStartState.Disabled;

        AutoStartNoteText.Text = state switch
        {
            // Windows does not let an app re-enable a startup task the user turned off.
            AutoStartState.DisabledByUser => "Turned off in Windows Settings. Turn it back on there.",
            AutoStartState.ManagedByPolicy => "This setting is managed by your organization.",
            null => "Windows did not report whether Headroom starts with Windows.",
            _ => "",
        };
        AutoStartNote.Visibility = AutoStartNoteText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        StartupAppsLink.Visibility = state is AutoStartState.DisabledByUser ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnAutoStartClick(object sender, RoutedEventArgs e)
    {
        var enabled = AutoStartBox.IsChecked == true;
        AutoStartBox.IsEnabled = false;
        try
        {
            ShowAutoStart(await AutoStart.SetAsync(enabled));
        }
        catch (Exception)
        {
            // Show whatever state Windows actually ended up with.
            await LoadAutoStartAsync();
        }
    }

    private async void OnStartupAppsClick(object sender, RoutedEventArgs e)
    {
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:startupapps"));
    }

    private void OnIntervalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || IntervalBox.SelectedItem is not ComboBoxItem { Tag: int minutes })
        {
            return;
        }

        _settings.RefreshIntervalMinutes = minutes;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnRefreshOnOpenClick(object sender, RoutedEventArgs e)
    {
        _settings.RefreshOnOpen = RefreshOnOpenBox.IsChecked == true;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }
}
