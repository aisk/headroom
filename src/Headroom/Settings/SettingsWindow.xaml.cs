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
/// Choose which tools are shown and how often usage refreshes. Changes apply immediately.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private const int WidthDip = 440;
    private const int HeightDip = 480;

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
