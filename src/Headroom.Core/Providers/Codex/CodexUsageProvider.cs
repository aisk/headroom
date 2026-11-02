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
        await using var rpc = JsonRpcProcess.Start(executable, ["-s", "read-only", "-a", "never", "app-server"], "codex app-server");

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
}
