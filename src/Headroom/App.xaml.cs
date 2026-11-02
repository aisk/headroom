using Headroom.Core.Providers.Antigravity;
using Headroom.Core.Providers.Claude;
using Headroom.Core.Providers.Codex;
using Headroom.Core.Providers.Grok;
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
            // Opening the app again (e.g. from a desktop shortcut) shows the running instance's panel.
            SignalRunningInstance();
            Exit();
            return;
        }

        _monitor = new UsageMonitor((IUsageProvider[])[new AntigravityUsageProvider(), new ClaudeUsageProvider(), new CodexUsageProvider(), new GrokUsageProvider()]);
        _viewModel = new MainViewModel(_monitor, DispatcherQueue.GetForCurrentThread());
        _flyout = new FlyoutWindow(_viewModel);

        _tray = new TrayIcon();
        _tray.Invoked += (_, _) => _flyout.Toggle(_tray.GetIconRect());
        _tray.ShowRequested += (_, _) => _flyout.ShowAt(_tray.GetIconRect());
        _tray.ContextMenuRequested += (_, anchor) => _ = ShowContextMenuAsync(anchor);
        _tray.AppearanceChanged += (_, _) => UpdateTray();
        _viewModel.Changed += (_, _) => UpdateTray();
        UpdateTray();

        _monitor.Start(RefreshInterval);

        // A launch by the user shows the panel right away; a startup launch stays in the tray.
        if (!AutoStart.IsStartupLaunch())
        {
            DispatcherQueue.GetForCurrentThread().TryEnqueue(DispatcherQueuePriority.Low, () => _flyout.ShowAt(_tray.GetIconRect()));
        }
    }

    private static void SignalRunningInstance()
    {
        var hwnd = Interop.NativeMethods.FindWindowW(TrayIcon.WindowClassName, null);
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // This process was started by the user and may hand its foreground right to the running instance.
        Interop.NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        Interop.NativeMethods.AllowSetForegroundWindow(processId);
        Interop.NativeMethods.PostMessageW(hwnd, TrayIcon.ShowRequestMessage, IntPtr.Zero, IntPtr.Zero);
    }

    private void UpdateTray()
    {
        if (_tray is null || _viewModel is null)
        {
            return;
        }

        _tray.Update(TrayIconRenderer.Render(_viewModel.TrayBars()), _viewModel.TrayTooltip());
    }

    private async Task ShowContextMenuAsync(Interop.NativeMethods.POINT anchor)
    {
        if (_tray is null)
        {
            return;
        }

        var autoStart = await GetAutoStartStateAsync();
        var command = _tray.ShowMenu(
        [
            new TrayMenuItem(MenuOpen, "Show usage"),
            new TrayMenuItem(MenuRefresh, "Refresh now"),
            TrayMenuItem.Separator,
            new TrayMenuItem(MenuAutoStart, AutoStartMenuText(autoStart),
                IsChecked: autoStart is AutoStartState.Enabled,
                IsEnabled: autoStart is not AutoStartState.ManagedByPolicy),
            TrayMenuItem.Separator,
            new TrayMenuItem(MenuQuit, "Quit Headroom"),
        ], anchor);

        switch (command)
        {
            case MenuOpen:
                _flyout?.ShowAt(_tray.GetIconRect());
                break;
            case MenuRefresh:
                _ = _viewModel?.RefreshAsync(manual: true);
                break;
            case MenuAutoStart:
                await ToggleAutoStartAsync(autoStart);
                break;
            case MenuQuit:
                Quit();
                break;
        }
    }

    private static string AutoStartMenuText(AutoStartState state) => state switch
    {
        AutoStartState.DisabledByUser => "Start with Windows (turn on in Settings)",
        AutoStartState.ManagedByPolicy => "Start with Windows (managed by your organization)",
        _ => "Start with Windows",
    };

    private static async Task<AutoStartState> GetAutoStartStateAsync()
    {
        try
        {
            return await AutoStart.GetStateAsync();
        }
        catch (Exception)
        {
            return AutoStartState.ManagedByPolicy; // Shown disabled rather than failing the menu.
        }
    }

    private static async Task ToggleAutoStartAsync(AutoStartState current)
    {
        try
        {
            if (current is AutoStartState.DisabledByUser)
            {
                // Windows does not let an app re-enable a startup task the user turned off.
                await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:startupapps"));
                return;
            }

            await AutoStart.SetAsync(current is not AutoStartState.Enabled);
        }
        catch (Exception)
        {
            // Leave the setting unchanged; the menu shows the actual state next time.
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
