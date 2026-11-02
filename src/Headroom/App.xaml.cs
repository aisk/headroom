using Headroom.Core.Providers.Antigravity;
using Headroom.Core.Providers.Claude;
using Headroom.Core.Providers.Codex;
using Headroom.Core.Usage;
using Headroom.Flyout;
using Headroom.Tray;
using Headroom.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Headroom;

/// <summary>
/// A windowless tray app: the only UI is the notification-area icon and its flyout.
/// </summary>
public partial class App : Application
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    private const uint MenuOpen = 1;
    private const uint MenuRefresh = 2;
    private const uint MenuAutoStart = 3;
    private const uint MenuQuit = 4;

    private Mutex? _singleInstance;
    private UsageMonitor? _monitor;
    private MainViewModel? _viewModel;
    private FlyoutWindow? _flyout;
    private TrayIcon? _tray;

    public App()
    {
        InitializeComponent();
        // There is no main window; keep running until the user quits from the tray menu.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\Headroom.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Exit();
            return;
        }

        _monitor = new UsageMonitor([new AntigravityUsageProvider(), new ClaudeUsageProvider(), new CodexUsageProvider()]);
        _viewModel = new MainViewModel(_monitor, DispatcherQueue.GetForCurrentThread());
        _flyout = new FlyoutWindow(_viewModel);

        _tray = new TrayIcon();
        _tray.Invoked += (_, _) => _flyout.Toggle(_tray.GetIconRect());
        _tray.ContextMenuRequested += (_, anchor) => ShowContextMenu(anchor);
        _tray.AppearanceChanged += (_, _) => UpdateTray();
        _viewModel.Changed += (_, _) => UpdateTray();
        UpdateTray();

        _monitor.Start(RefreshInterval);
    }

    private void UpdateTray()
    {
        if (_tray is null || _viewModel is null)
        {
            return;
        }

        _tray.Update(TrayIconRenderer.Render(_viewModel.TrayBars()), _viewModel.TrayTooltip());
    }

    private void ShowContextMenu(Interop.NativeMethods.POINT anchor)
    {
        if (_tray is null)
        {
            return;
        }

        var command = _tray.ShowMenu(
        [
            new TrayMenuItem(MenuOpen, "Show usage"),
            new TrayMenuItem(MenuRefresh, "Refresh now"),
            TrayMenuItem.Separator,
            new TrayMenuItem(MenuAutoStart, "Start with Windows", IsChecked: AutoStart.IsEnabled),
            TrayMenuItem.Separator,
            new TrayMenuItem(MenuQuit, "Quit Headroom"),
        ], anchor);

        switch (command)
        {
            case MenuOpen:
                _flyout?.ShowAt(_tray.GetIconRect());
                break;
            case MenuRefresh:
                _ = _viewModel?.RefreshAsync();
                break;
            case MenuAutoStart:
                AutoStart.Set(!AutoStart.IsEnabled);
                break;
            case MenuQuit:
                Quit();
                break;
        }
    }

    private void Quit()
    {
        _monitor?.Dispose();
        _tray?.Dispose();
        _tray = null;
        _flyout?.CloseForExit();
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        Exit();
    }
}
