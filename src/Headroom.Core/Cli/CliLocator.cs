using Microsoft.Win32;

namespace Headroom.Core.Cli;

/// <summary>
/// Resolves CLI executables. GUI processes often inherit a stale PATH, so the registry PATH and
/// common per-user install directories are searched as well.
/// </summary>
public static class CliLocator
{
    private static readonly string[] Extensions = [".exe", ".cmd", ".bat"];

    public static string? Find(string name, string? overrideEnvironmentVariable = null)
    {
        if (overrideEnvironmentVariable is not null)
        {
            var explicitPath = Environment.GetEnvironmentVariable(overrideEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                // An explicit override is authoritative; never fall back to discovery.
                return File.Exists(explicitPath) ? explicitPath : null;
            }
        }

        foreach (var directory in SearchDirectories())
        {
            foreach (var extension in Extensions)
            {
                var candidate = Path.Combine(directory, name + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> SearchDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new List<string?>
        {
            Environment.GetEnvironmentVariable("PATH"),
            ReadRegistryPath(Registry.CurrentUser, "Environment"),
            ReadRegistryPath(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"),
        };

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        paths.Add(string.Join(';',
            Path.Combine(home, ".local", "bin"),
            Path.Combine(localAppData, "Microsoft", "WinGet", "Links"),
            Path.Combine(appData, "npm"),
            Path.Combine(localAppData, "Programs", "Antigravity", "bin")));

        foreach (var pathList in paths)
        {
            if (string.IsNullOrEmpty(pathList))
            {
                continue;
            }

            foreach (var raw in pathList.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var directory = Environment.ExpandEnvironmentVariables(raw.Trim('"'));
                // Relative PATH entries would resolve against our working directory; ignore them.
                if (!Path.IsPathFullyQualified(directory) || !seen.Add(directory))
                {
                    continue;
                }

                yield return directory;
            }
        }
    }

    private static string? ReadRegistryPath(RegistryKey root, string subKey)
    {
        try
        {
            using var key = root.OpenSubKey(subKey);
            return key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
