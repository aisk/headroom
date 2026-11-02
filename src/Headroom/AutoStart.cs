using Microsoft.Win32;

namespace Headroom;

/// <summary>Per-user "start with Windows" registration via the Run key.</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Headroom";

    /// <summary>Passed by the Run key so a startup launch stays in the tray instead of opening the panel.</summary>
    public const string Argument = "--autostart";

    private static string Command => $"\"{Environment.ProcessPath}\" {Argument}";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            // Accept registrations with or without arguments, as long as they point at this executable.
            return key?.GetValue(ValueName) is string value &&
                   value.StartsWith($"\"{Environment.ProcessPath}\"", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
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
    }
}
