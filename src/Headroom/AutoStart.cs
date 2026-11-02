using Microsoft.Win32;

namespace Headroom;

/// <summary>Per-user "start with Windows" registration via the Run key.</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Headroom";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
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
