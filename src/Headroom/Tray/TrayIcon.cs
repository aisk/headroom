using System.Runtime.InteropServices;
using Headroom.Interop;
using static Headroom.Interop.NativeMethods;

namespace Headroom.Tray;

internal sealed record TrayMenuItem(uint Id, string Text, bool IsChecked = false, bool IsEnabled = true)
{
    public static readonly TrayMenuItem Separator = new(0, string.Empty);
}

/// <summary>
/// A notification-area icon (next to volume/network) backed by a hidden Win32 window.
/// Must be created and used on the UI thread, whose message loop pumps the hidden window.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const uint CallbackMessage = WM_APP + 1;

    /// <summary>Posted by a second instance to ask the running one to open its panel.</summary>
    public const uint ShowRequestMessage = WM_APP + 2;

    public const string WindowClassName = ClassName;
    private const string ClassName = "Headroom.TrayIconWindow";

    private readonly WndProc _wndProc;
    private readonly IntPtr _instance;
    private readonly uint _taskbarCreatedMessage;
    private IntPtr _hwnd;
    private IntPtr _icon;
    private string _tooltip = "Headroom";
    private bool _added;

    public TrayIcon()
    {
        _wndProc = WindowProc; // Keep the delegate alive for the lifetime of the window.
        _instance = GetModuleHandleW(null);
        var windowClass = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _instance,
            lpszClassName = ClassName,
        };
        RegisterClassExW(ref windowClass);

        // A hidden top-level popup (not message-only) so it receives the TaskbarCreated broadcast.
        _hwnd = CreateWindowExW(0, ClassName, "Headroom", WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Failed to create tray window (error {Marshal.GetLastWin32Error()}).");
        }

        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
    }

    /// <summary>Left click or keyboard activation on the icon.</summary>
    public event EventHandler? Invoked;

    /// <summary>Another launch of the app asked this instance to show its panel.</summary>
    public event EventHandler? ShowRequested;

    /// <summary>Right click; the argument is the anchor point in screen coordinates.</summary>
    public event EventHandler<POINT>? ContextMenuRequested;

    /// <summary>The taskbar was recreated or its theme/DPI changed; the icon should be re-rendered.</summary>
    public event EventHandler? AppearanceChanged;

    public IntPtr Handle => _hwnd;

    public void Update(IntPtr icon, string tooltip)
    {
        if (_icon != IntPtr.Zero && _icon != icon)
        {
            DestroyIcon(_icon);
        }

        _icon = icon;
        // The tooltip buffer holds 128 characters including the terminator.
        _tooltip = tooltip.Length > 127 ? tooltip[..126] + "…" : tooltip;
        Apply();
    }

    public RECT? GetIconRect()
    {
        var identifier = new NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = _hwnd,
            uID = IconId,
        };
        return Shell_NotifyIconGetRect(ref identifier, out var rect) == 0 ? rect : null;
    }

    /// <summary>Shows a native context menu and returns the chosen item id, or 0.</summary>
    public uint ShowMenu(TrayMenuItem[] items, POINT anchor)
    {
        var menu = CreatePopupMenu();
        try
        {
            foreach (var item in items)
            {
                if (ReferenceEquals(item, TrayMenuItem.Separator))
                {
                    AppendMenuW(menu, MF_SEPARATOR, 0, null);
                    continue;
                }

                var flags = MF_STRING | (item.IsChecked ? MF_CHECKED : 0) | (item.IsEnabled ? 0 : MF_GRAYED);
                AppendMenuW(menu, flags, item.Id, item.Text);
            }

            // Required so the menu closes when the user clicks elsewhere.
            SetForegroundWindow(_hwnd);
            var command = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY | TPM_BOTTOMALIGN,
                anchor.X, anchor.Y, _hwnd, IntPtr.Zero);
            PostMessageW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            return (uint)Math.Max(0, command);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void Apply()
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = _tooltip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
            uVersion = NOTIFYICON_VERSION_4,
        };

        if (_added && Shell_NotifyIconW(NIM_MODIFY, ref data))
        {
            return;
        }

        if (Shell_NotifyIconW(NIM_ADD, ref data))
        {
            Shell_NotifyIconW(NIM_SETVERSION, ref data);
            _added = true;
        }
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMessage)
        {
            // NOTIFYICON_VERSION_4: LOWORD(lParam) is the event, wParam holds the anchor coordinates.
            var notification = (uint)LowWord(lParam);
            switch (notification)
            {
                case NIN_SELECT:
                case NIN_KEYSELECT:
                    Invoked?.Invoke(this, EventArgs.Empty);
                    break;
                case WM_CONTEXTMENU:
                    ContextMenuRequested?.Invoke(this, new POINT { X = LowWord(wParam), Y = HighWord(wParam) });
                    break;
            }

            return IntPtr.Zero;
        }

        if (msg == ShowRequestMessage)
        {
            ShowRequested?.Invoke(this, EventArgs.Empty);
            return IntPtr.Zero;
        }

        if (msg == _taskbarCreatedMessage)
        {
            // Explorer restarted: the icon must be added again.
            _added = false;
            Apply();
            AppearanceChanged?.Invoke(this, EventArgs.Empty);
            return IntPtr.Zero;
        }

        if (msg == WM_SETTINGCHANGE || msg == WM_DPICHANGED)
        {
            AppearanceChanged?.Invoke(this, EventArgs.Empty);
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = IconId,
            szTip = string.Empty,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };
        Shell_NotifyIconW(NIM_DELETE, ref data);
        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }

        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        UnregisterClassW(ClassName, _instance);
    }
}
