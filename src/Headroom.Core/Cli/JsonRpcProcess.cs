using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Headroom.Core.Usage;

namespace Headroom.Core.Cli;

/// <summary>A JSON-RPC error response returned by the CLI.</summary>
public sealed class JsonRpcErrorException(string method, int code, string message)
    : UsageProviderException($"{method}: {message}")
{
    public const int MethodNotFound = -32601;

    public string Method { get; } = method;

    public int Code { get; } = code;
}

/// <summary>
/// A CLI child process speaking newline-delimited JSON-RPC 2.0 over stdin/stdout
/// (used by <c>codex app-server</c> and <c>grok agent stdio</c>).
/// </summary>
public sealed class JsonRpcProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _stderr = new();
    private readonly string _name;
    private int _nextId;

    private JsonRpcProcess(Process process, string name)
    {
        _process = process;
        _name = name;
    }

    public static JsonRpcProcess Start(string executable, IEnumerable<string> arguments, string displayName,
        string? workingDirectory = null, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var info = CliProcess.CreateStartInfo(executable, arguments, workingDirectory, environment);
        var process = new Process { StartInfo = info };
        var rpc = new JsonRpcProcess(process, displayName);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (rpc._stderr)
            {
                if (rpc._stderr.Length < 16 * 1024)
                {
                    rpc._stderr.AppendLine(e.Data);
                }
            }
        };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            process.Dispose();
            throw new UsageProviderException($"Failed to start {displayName}: {ex.Message}", ex);
        }

        process.BeginErrorReadLine();
        return rpc;
    }

    public async Task NotifyAsync(string method, CancellationToken cancellationToken)
    {
        await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method }, cancellationToken).ConfigureAwait(false);
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
                var line = await _process.StandardOutput.ReadLineAsync(timeoutSource.Token).ConfigureAwait(false);
                if (line is null)
                {
                    var detail = CliProcess.FirstLine(LastStderrLine());
                    throw new UsageProviderException(
                        detail is null ? $"{_name} exited unexpectedly." : $"{_name} exited: {detail}");
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

                // Skip notifications, server-initiated requests and responses to other requests.
                if (response?["id"] is not JsonValue idNode || !idNode.TryGetValue<int>(out var responseId) ||
                    responseId != id || response.ContainsKey("method"))
                {
                    continue;
                }

                if (response["error"] is JsonObject error)
                {
                    var code = error["code"] is JsonValue c && c.TryGetValue<int>(out var value) ? value : 0;
                    throw new JsonRpcErrorException(method, code, error["message"]?.ToString() ?? error.ToJsonString());
                }

                return response["result"];
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UsageProviderException($"{_name} did not answer {method} within {timeout.TotalSeconds:0}s.");
        }
        catch (IOException ex)
        {
            throw new UsageProviderException($"{_name} connection failed: {ex.Message}", ex);
        }
    }

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var input = _process.StandardInput;
        await input.WriteLineAsync(message.ToJsonString().AsMemory(), cancellationToken).ConfigureAwait(false);
        await input.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private string LastStderrLine()
    {
        string text;
        lock (_stderr)
        {
            text = _stderr.ToString();
        }

        // The most recent ERROR line is usually the most useful one.
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.LastOrDefault(l => l.Contains("ERROR", StringComparison.Ordinal)) ?? lines.LastOrDefault() ?? string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // Closing stdin asks the server to shut down; give it a moment before killing it.
            _process.StandardInput.Close();
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Fall through to a forced kill.
        }

        CliProcess.KillTree(_process);
        _process.Dispose();
    }
}
