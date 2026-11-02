using Headroom.Core.Cli;
using Headroom.Core.Usage;

namespace Headroom.Core.Providers.Antigravity;

/// <summary>
/// Reads Antigravity quota through the official non-interactive report:
/// <c>agy -p /usage --output-format json</c>. The report is a local command, so no model prompt is sent.
/// </summary>
public sealed class AntigravityUsageProvider : IUsageProvider
{
    // Non-interactive usage reports were introduced in agy 1.1.11.
    private static readonly Version MinimumVersion = new(1, 1, 11);
    private const string OverrideVariable = "ANTIGRAVITY_CLI_PATH";

    public string Id => "antigravity";

    public string DisplayName => "Antigravity";

    public bool IsInstalled() => CliLocator.Find("agy", OverrideVariable) is not null;

    public async Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var executable = CliLocator.Find("agy", OverrideVariable)
            ?? throw new UsageProviderException("agy CLI not found. Install Antigravity CLI and run `agy` once to sign in.");

        var probeDirectory = Directory.CreateTempSubdirectory("headroom-agy-").FullName;
        try
        {
            var environment = CreateEnvironment(probeDirectory);

            var versionResult = await CliProcess.RunAsync(
                CliProcess.CreateStartInfo(executable, ["--version"], probeDirectory, environment),
                TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);
            var version = CliProcess.ParseVersion(versionResult.StandardOutput);
            // Older builds would treat "/usage" as a model prompt; never risk spending tokens on an unknown version.
            if (version is null)
            {
                throw new UsageProviderException("Could not determine the agy version; skipping the usage report.");
            }

            if (version < MinimumVersion)
            {
                throw new UsageProviderException($"agy {version} is too old for usage reports; update to {MinimumVersion} or later.");
            }

            var result = await CliProcess.RunAsync(
                CliProcess.CreateStartInfo(executable, ["-p", "/usage", "--output-format", "json"], probeDirectory, environment),
                TimeSpan.FromSeconds(90),
                cancellationToken).ConfigureAwait(false);

            var json = ExtractJsonObject(result.StandardOutput);
            if (json is null)
            {
                var detail = CliProcess.FirstLine(result.StandardError) ?? CliProcess.FirstLine(result.StandardOutput);
                throw new UsageProviderException(
                    detail is null
                        ? $"agy exited with code {result.ExitCode} and printed no usage report."
                        : $"agy: {detail}");
            }

            return AntigravityUsageParser.Parse(json, DateTimeOffset.Now);
        }
        finally
        {
            TryDeleteDirectory(probeDirectory);
        }
    }

    private static Dictionary<string, string?> CreateEnvironment(string probeDirectory)
    {
        // A launcher that always fails keeps a background probe from opening browser sign-in tabs.
        var failingLauncher = Path.Combine(probeDirectory, "no-browser.cmd");
        File.WriteAllText(failingLauncher, "@exit /b 1\r\n");

        return new Dictionary<string, string?>
        {
            // Passive probes must never trigger a CLI self-update. agy only accepts the literal "true".
            ["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true",
            ["BROWSER"] = failingLauncher,
            ["NO_COLOR"] = "1",
        };
    }

    private static string? ExtractJsonObject(string output)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        return start >= 0 && end > start ? output[start..(end + 1)] : null;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
            // Leftover temp directories are harmless; the OS cleans them up eventually.
        }
    }
}
