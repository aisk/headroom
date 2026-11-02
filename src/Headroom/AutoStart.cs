using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Windows.AppLifecycle;
using StartupTask = Windows.ApplicationModel.StartupTask;
using StartupTaskState = Windows.ApplicationModel.StartupTaskState;

namespace Headroom;

internal enum AutoStartState
{
    Disabled,
    Enabled,
    /// <summary>Turned off in Settings or Task Manager; only the user can turn it back on there.</summary>
    DisabledByUser,
    /// <summary>Forced on or off by group policy.</summary>
    ManagedByPolicy,
}

/// <summary>
/// Per-user "start with Windows": a StartupTask when running as an MSIX package (the Store build),
/// the Run key otherwise.
/// </summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Headroom";

    /// <summary>Must match the TaskId in Package.appxmanifest.</summary>
    private const string StartupTaskId = "HeadroomStartup";

    /// <summary>Passed by the Run key so a startup launch stays in the tray instead of opening the panel.</summary>
    public const string Argument = "--autostart";

    private static string Command => $"\"{Environment.ProcessPath}\" {Argument}";

    public static bool IsPackaged { get; } = DetectPackaged();

    /// <summary>True when this process was started by Windows at sign-in rather than by the user.</summary>
    public static bool IsStartupLaunch()
    {
        if (Environment.GetCommandLineArgs().Contains(Argument, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsPackaged && AppInstance.GetCurrent().GetActivatedEventArgs().Kind == ExtendedActivationKind.StartupTask;
    }

    public static async Task<AutoStartState> GetStateAsync()
    {
        if (!IsPackaged)
        {
            return IsRunKeySet() ? AutoStartState.Enabled : AutoStartState.Disabled;
        }

        var task = await StartupTask.GetAsync(StartupTaskId);
        return Map(task.State);
    }

    /// <summary>Turns auto start on or off and returns the resulting state.</summary>
    public static async Task<AutoStartState> SetAsync(bool enabled)
    {
        if (!IsPackaged)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                key.SetValue(ValueName, Command);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return enabled ? AutoStartState.Enabled : AutoStartState.Disabled;
        }

        var task = await StartupTask.GetAsync(StartupTaskId);
        if (enabled && task.State == StartupTaskState.Disabled)
        {
            return Map(await task.RequestEnableAsync());
        }

        if (!enabled && task.State == StartupTaskState.Enabled)
        {
            task.Disable();
        }

        return Map(task.State);
    }

    private static AutoStartState Map(StartupTaskState state) => state switch
    {
        StartupTaskState.Enabled => AutoStartState.Enabled,
        StartupTaskState.DisabledByUser => AutoStartState.DisabledByUser,
        StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy => AutoStartState.ManagedByPolicy,
        _ => AutoStartState.Disabled,
    };

    private static bool IsRunKeySet()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        // Accept registrations with or without arguments, as long as they point at this executable.
        return key?.GetValue(ValueName) is string value &&
               value.StartsWith($"\"{Environment.ProcessPath}\"", StringComparison.OrdinalIgnoreCase);
    }

    private static bool DetectPackaged()
    {
        const int AppModelErrorNoPackage = 15700;
        var length = 0u;
        return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);
}
