using System.Diagnostics;
using System.Text;
using Headroom.Core.Usage;

namespace Headroom.Core.Cli;

public sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Launches CLI tools hidden, with stdin closed, and with bounded runtime.
/// </summary>
public static class CliProcess
{
    private const int MaxOutputChars = 1024 * 1024;

    public static ProcessStartInfo CreateStartInfo(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        ProcessStartInfo info;
        var extension = Path.GetExtension(executable);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            // npm shims are batch files and must run through cmd.exe.
            var commandLine = string.Join(' ', new[] { executable }.Concat(arguments).Select(QuoteForCmd));
            info = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
            {
                Arguments = $"/d /s /c \"{commandLine}\"",
            };
        }
        else
        {
            info = new ProcessStartInfo(executable);
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }
        }

        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;
        if (workingDirectory is not null)
        {
            info.WorkingDirectory = workingDirectory;
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                if (value is null)
                {
                    info.Environment.Remove(key);
                }
                else
                {
                    info.Environment[key] = value;
                }
            }
        }

        return info;
    }

    public static async Task<CliResult> RunAsync(ProcessStartInfo info, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start())
            {
                throw new UsageProviderException($"Failed to start {Path.GetFileName(info.FileName)}.");
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new UsageProviderException($"Failed to start {Path.GetFileName(info.FileName)}: {ex.Message}", ex);
        }

        // Closing stdin makes interactive CLIs behave as non-interactive and never wait for input.
        process.StandardInput.Close();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, timeoutSource.Token);
        var stderrTask = ReadBoundedAsync(process.StandardError, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new CliResult(process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UsageProviderException($"{Path.GetFileNameWithoutExtension(info.FileName)} timed out after {timeout.TotalSeconds:0}s.");
        }
        finally
        {
            KillTree(process);
        }
    }

    public static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Best effort: the process may have exited concurrently.
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (builder.Length < MaxOutputChars)
            {
                builder.Append(buffer, 0, Math.Min(read, MaxOutputChars - builder.Length));
            }
        }

        return builder.ToString();
    }

    private static string QuoteForCmd(string value) =>
        value.Length > 0 && value.IndexOfAny([' ', '\t', '&', '(', ')', '^', '|', '<', '>']) < 0
            ? value
            : "\"" + value.Replace("\"", "\"\"") + "\"";

    /// <summary>Returns the first meaningful line of CLI output, for error messages.</summary>
    public static string? FirstLine(string? text, int maxLength = 200)
    {
        var line = text?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.Length > 0);
        if (line is null)
        {
            return null;
        }

        return line.Length <= maxLength ? line : line[..maxLength] + "…";
    }

    /// <summary>Parses the leading "major.minor.patch" from version output.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(text, @"(\d+)\.(\d+)(?:\.(\d+))?");
        if (!match.Success)
        {
            return null;
        }

        return new Version(
            int.Parse(match.Groups[1].Value),
            int.Parse(match.Groups[2].Value),
            match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0);
    }
}
