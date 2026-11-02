# Headroom

A small WinUI 3 tray app for Windows that shows how much quota is left in your AI coding CLIs.
It has no main window: it lives in the notification area next to the volume icon, and clicking
the icon opens a flyout with the usage of each tool.

The tray icon draws one bar per tool, filled with the remaining quota of its most constrained window.

## Data sources

Headroom only uses the official CLIs, never private web endpoints or stored tokens, and never sends a
model prompt, so polling does not consume tokens.

| Tool | How usage is read |
| --- | --- |
| Antigravity | `agy -p /usage --output-format json` (structured report, agy ≥ 1.1.11). Auto-update is disabled for the probe and browser sign-in is blocked. |
| Claude Code | `claude /usage` run non-interactively in a dedicated probe directory, with hooks and MCP servers disabled for that process; probe transcripts are removed afterwards. Requires Claude Code ≥ 2.0. |
| Codex | `codex app-server` JSON-RPC (`account/read`, `account/rateLimits/read`) with a read-only sandbox. |
| Grok | `grok agent stdio` (Agent Client Protocol) `initialize` + the `_x.ai/billing` extension. No session is created. Plans that publish no usage percentage (e.g. Free) show only the plan and billing period. |

Tools whose CLI cannot be found are hidden. Usage refreshes every 5 minutes, when the flyout is
opened with data older than 2 minutes, or from the refresh button / tray menu.

## Build

Requires the .NET 10 SDK on Windows 10 2004 or later.

```powershell
dotnet build src/Headroom -p:Platform=x64
dotnet test tests/Headroom.Core.Tests
```

The app is unpackaged and self-contained with the Windows App SDK. Right-click the tray icon for
**Refresh now**, **Start with Windows**, and **Quit**. New tray icons start in the overflow area;
drag it next to the clock, or enable it under *Settings → Personalization → Taskbar → Other system tray icons*.

## Layout

- `src/Headroom.Core` – CLI discovery, providers and parsers (no UI dependencies).
- `src/Headroom` – WinUI 3 app: tray icon (Win32 `Shell_NotifyIcon`), flyout window, view models.
- `tests/Headroom.Core.Tests` – parser tests based on real CLI output.
