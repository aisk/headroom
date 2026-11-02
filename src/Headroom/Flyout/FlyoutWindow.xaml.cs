using Headroom.Interop;
using Headroom.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;
using static Headroom.Interop.NativeMethods;

namespace Headroom.Flyout;

/// <summary>
/// A borderless, always-on-top panel that pops up above the notification area, like the
/// volume and network flyouts, and hides itself as soon as it loses focus.
/// </summary>
public sealed partial class FlyoutWindow : Window
{
    private const double WidthDip = 360;
    private const double MarginDip = 12;

    private readonly IntPtr _hwnd;
    private long _lastHiddenTicks;
    private bool _allowClose;
    private bool _wasActivated;
    private RECT? _anchor;

    public FlyoutWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Closing += (_, args) =>
        {
            if (!_allowClose)
            {
                args.Cancel = true;
                Hide();
            }
        };

        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                _wasActivated = true;
            }
            else if (_wasActivated)
            {
                // Only light-dismiss after the flyout actually got focus; if Windows refused to
                // bring it to the foreground it stays open until toggled or dismissed with Escape.
                Hide();
            }
        };
    }

    public MainViewModel ViewModel { get; }

    public bool IsShown => AppWindow.IsVisible;

    /// <summary>Toggles the flyout; clicking the tray icon while it is open closes it.</summary>
    internal void Toggle(RECT? anchor)
    {
        if (IsShown)
        {
            Hide();
            return;
        }

        // A click on the tray icon first deactivates (and hides) the open flyout; don't reopen it.
        if (Environment.TickCount64 - _lastHiddenTicks < 300)
        {
            return;
        }

        ShowAt(anchor);
    }

    internal void ShowAt(RECT? anchor)
    {
        _anchor = anchor;
        _wasActivated = false;
        ViewModel.RefreshTexts();
        Reposition();
        Activate();
        SetForegroundWindow(_hwnd);
        // Pointer focus state: keeps Escape working without drawing a keyboard focus rectangle.
        Scroller.Focus(FocusState.Pointer);
        // Opening the flyout refreshes stale data without hammering the CLIs.
        _ = ViewModel.RefreshAsync(maxAge: TimeSpan.FromMinutes(2));
    }

    public void Hide()
    {
        if (!AppWindow.IsVisible)
        {
            return;
        }

        AppWindow.Hide();
        _lastHiddenTicks = Environment.TickCount64;
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    private void Reposition()
    {
        var point = AnchorPoint();
        var display = DisplayArea.GetFromPoint(new PointInt32(point.X, point.Y), DisplayAreaFallback.Nearest);
        var work = display.WorkArea;
        var outer = display.OuterBounds;
        var scale = MonitorScale(point);

        var width = (int)Math.Round(WidthDip * scale);
        var margin = (int)Math.Round(MarginDip * scale);
        // Measure at the final width: while hidden the panel may have been laid out at another width.
        var contentHeight = MeasureContentHeight();
        // The window size includes the thin border, so add the non-client height on top of the content.
        var frame = Math.Max(0, AppWindow.Size.Height - AppWindow.ClientSize.Height);
        var height = Math.Min((int)Math.Ceiling(contentHeight * scale) + frame, work.Height - 2 * margin);

        // Align with the taskbar edge, like the built-in Windows 11 flyouts.
        var x = work.X + work.Width - width - margin;
        var y = work.Y + work.Height - height - margin;
        if (work.Y > outer.Y)
        {
            y = work.Y + margin; // Taskbar on top.
        }
        else if (work.X > outer.X)
        {
            x = work.X + margin; // Taskbar on the left.
        }

        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private double MeasureContentHeight()
    {
        ContentPanel.Measure(new Windows.Foundation.Size(WidthDip, double.PositiveInfinity));
        return Math.Max(ContentPanel.DesiredSize.Height, 120);
    }

    private POINT AnchorPoint()
    {
        if (_anchor is { } rect)
        {
            return new POINT { X = (rect.Left + rect.Right) / 2, Y = (rect.Top + rect.Bottom) / 2 };
        }

        GetCursorPos(out var cursor);
        return cursor;
    }

    private static double MonitorScale(POINT point)
    {
        var monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
        return GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
    }

    private void OnContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsShown && Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 0.5)
        {
            Reposition();
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = ViewModel.RefreshAsync();

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }
}
