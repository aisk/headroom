using System.Text.RegularExpressions;
using Headroom.Core.Cli;
using Headroom.Core.Usage;

namespace Headroom.Core.Providers.Claude;

/// <summary>
/// Reads Claude subscription usage by running Claude Code's built-in <c>/usage</c> command
/// non-interactively. <c>/usage</c> is a local slash command, so it does not send a model prompt.
/// </summary>
public sealed class ClaudeUsageProvider : IUsageProvider
{
    // Guard against old CLIs that might treat "/usage" as a prompt and spend tokens.
    private static readonly Version MinimumVersion = new(2, 0, 0);
    private const string ProbeDirectoryName = "ClaudeProbe";
    private const string ProbeSettings = """{"remoteControlAtStartup":false,"disableAllHooks":true}""";

    public string Id => "claude";

    public string DisplayName => "Claude";

    public bool IsInstalled() => CliLocator.Find("claude") is not null;

    public async Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var executable = CliLocator.Find("claude")
            ?? throw new UsageProviderException("Claude Code not found. Install it and run `claude` once to sign in.");

        var probeDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Headroom", ProbeDirectoryName);
        Directory.CreateDirectory(probeDirectory);
        var settingsPath = Path.Combine(probeDirectory, "probe-settings.json");
        await File.WriteAllTextAsync(settingsPath, ProbeSettings, cancellationToken).ConfigureAwait(false);

        try
        {
            var versionResult = await CliProcess.RunAsync(
                CliProcess.CreateStartInfo(executable, ["--version"], probeDirectory),
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
            var version = CliProcess.ParseVersion(versionResult.StandardOutput);
            if (version is null)
            {
                throw new UsageProviderException("Could not determine the Claude Code version; skipping /usage.");
            }

            if (version < MinimumVersion)
            {
                throw new UsageProviderException($"Claude Code {version} is too old for /usage; update to {MinimumVersion} or later.");
            }

            // --strict-mcp-config skips the user's MCP servers; the settings disable hooks and remote control
            // for this process only, leaving saved settings untouched.
            var result = await CliProcess.RunAsync(
                CliProcess.CreateStartInfo(
                    executable,
                    ["--strict-mcp-config", "--settings", settingsPath, "/usage"],
                    probeDirectory,
                    new Dictionary<string, string?> { ["NO_COLOR"] = "1" }),
                TimeSpan.FromSeconds(60),
                cancellationToken).ConfigureAwait(false);

            var output = string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardError : result.StandardOutput;
            return ClaudeUsageParser.Parse(output, DateTimeOffset.Now);
        }
        finally
        {
            CleanupProbeTranscripts(probeDirectory);
        }
    }

    /// <summary>
    /// Claude Code records every run as a project transcript. Remove the ones created by our dedicated
    /// probe directory so background polling does not clutter the user's history.
    /// </summary>
    private static void CleanupProbeTranscripts(string probeDirectory)
    {
        try
        {
            var configDirectory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(configDirectory))
            {
                configDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            }

            var projectName = Regex.Replace(Path.GetFullPath(probeDirectory), "[^a-zA-Z0-9]", "-");
            // Only ever delete the folder that belongs to our own probe directory.
            if (!projectName.EndsWith("-Headroom-" + ProbeDirectoryName, StringComparison.Ordinal))
            {
                return;
            }

            var projectDirectory = Path.Combine(configDirectory, "projects", projectName);
            if (Directory.Exists(projectDirectory))
            {
                Directory.Delete(projectDirectory, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best effort; a leftover transcript is harmless.
        }
    }
}
