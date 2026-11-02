using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Headroom.Core.Cli;
using Headroom.Core.Usage;

namespace Headroom.Core.Providers.Codex;

/// <summary>
/// Reads Codex rate limits from the CLI's own JSON-RPC server (<c>codex app-server</c>),
/// the same channel the official IDE integrations use. No model turn is started.
/// </summary>
public sealed class CodexUsageProvider : IUsageProvider
{
    private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    public string Id => "codex";

    public string DisplayName => "Codex";

    public bool IsInstalled() => CliLocator.Find("codex") is not null;

    public async Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var executable = CliLocator.Find("codex")
            ?? throw new UsageProviderException("Codex CLI not found. Install it and run `codex login`.");

        // Read-only sandbox and no approvals: the server is only used for account queries.
        var info = CliProcess.CreateStartInfo(executable, ["-s", "read-only", "-a", "never", "app-server"]);
        using var process = new Process { StartInfo = info };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null && stderr.Length < 16 * 1024)
            {
                lock (stderr)
                {
                    stderr.AppendLine(e.Data);
                }
            }
        };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new UsageProviderException($"Failed to start codex: {ex.Message}", ex);
        }

        process.BeginErrorReadLine();
        try
        {
            var rpc = new JsonRpcLineClient(process.StandardInput, process.StandardOutput, () =>
            {
                lock (stderr)
                {
                    return stderr.ToString();
                }
            });

            await rpc.RequestAsync(
                "initialize",
                new JsonObject { ["clientInfo"] = new JsonObject { ["name"] = "headroom", ["version"] = "0.1.0" } },
                InitializeTimeout,
                cancellationToken).ConfigureAwait(false);
            await rpc.NotifyAsync("initialized", cancellationToken).ConfigureAwait(false);

            JsonNode? account = null;
            try
            {
                account = await rpc.RequestAsync("account/read", new JsonObject(), RequestTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (UsageProviderException)
            {
                // Identity is optional; rate limits are what matter.
            }

            if (account?["account"] is null && account?["requiresOpenaiAuth"]?.GetValue<bool>() == true)
            {
                throw new UsageProviderException("Codex is not signed in. Run `codex login`.");
            }

            var limits = await rpc.RequestAsync("account/rateLimits/read", null, RequestTimeout, cancellationToken)
                .ConfigureAwait(false);
            return CodexRateLimitParser.Parse(limits, account, DateTimeOffset.Now);
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Fall through to a forced kill.
            }

            CliProcess.KillTree(process);
        }
    }

    /// <summary>Minimal newline-delimited JSON-RPC 2.0 client.</summary>
    private sealed class JsonRpcLineClient(StreamWriter input, StreamReader output, Func<string> readStderr)
    {
        private int _nextId;

        public async Task NotifyAsync(string method, CancellationToken cancellationToken)
        {
            var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
            await WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }

        public async Task<JsonNode?> RequestAsync(string method, JsonNode? parameters, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var id = Interlocked.Increment(ref _nextId);
            var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (parameters is not null)
            {
                message["params"] = parameters;
            }

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await WriteAsync(message, timeoutSource.Token).ConfigureAwait(false);
                while (true)
                {
                    var line = await output.ReadLineAsync(timeoutSource.Token).ConfigureAwait(false);
                    if (line is null)
                    {
                        var detail = CliProcess.FirstLine(LastLines(readStderr()));
                        throw new UsageProviderException(
                            detail is null ? "codex app-server exited unexpectedly." : $"codex app-server exited: {detail}");
                    }

                    JsonObject? response;
                    try
                    {
                        response = JsonNode.Parse(line) as JsonObject;
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    // Skip notifications and responses to other requests.
                    if (response?["id"] is not JsonValue idNode || !idNode.TryGetValue<int>(out var responseId) || responseId != id)
                    {
                        continue;
                    }

                    if (response["error"] is JsonObject error)
                    {
                        throw new UsageProviderException($"codex {method}: {error["message"] ?? error.ToJsonString()}");
                    }

                    return response["result"];
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new UsageProviderException($"codex app-server did not answer {method} within {timeout.TotalSeconds:0}s.");
            }
            catch (IOException ex)
            {
                throw new UsageProviderException($"codex app-server connection failed: {ex.Message}", ex);
            }
        }

        private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
        {
            await input.WriteLineAsync(message.ToJsonString().AsMemory(), cancellationToken).ConfigureAwait(false);
            await input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private static string LastLines(string text)
        {
            // The most recent ERROR line is usually the most useful one.
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines.LastOrDefault(l => l.Contains("ERROR", StringComparison.Ordinal)) ?? lines.LastOrDefault() ?? string.Empty;
        }
    }
}
