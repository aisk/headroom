using System.Text.Json.Nodes;
using Headroom.Core.Cli;
using Headroom.Core.Usage;

namespace Headroom.Core.Providers.Grok;

/// <summary>
/// Reads Grok Build billing through the CLI's Agent Client Protocol server (<c>grok agent stdio</c>)
/// and its <c>_x.ai/billing</c> extension. No session is created and no prompt is sent.
/// </summary>
public sealed class GrokUsageProvider : IUsageProvider
{
    private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    public string Id => "grok";

    public string DisplayName => "Grok";

    public bool IsInstalled() => CliLocator.Find("grok") is not null;

    public async Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var executable = CliLocator.Find("grok")
            ?? throw new UsageProviderException("Grok CLI not found. Install Grok Build and run `grok login`.");

        await using var rpc = JsonRpcProcess.Start(executable, ["agent", "stdio"], "grok agent");

        await rpc.RequestAsync(
            "initialize",
            new JsonObject { ["protocolVersion"] = 1, ["clientCapabilities"] = new JsonObject() },
            InitializeTimeout,
            cancellationToken).ConfigureAwait(false);

        JsonNode? billing;
        try
        {
            billing = await rpc.RequestAsync("_x.ai/billing", new JsonObject(), RequestTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonRpcErrorException ex) when (ex.Code == JsonRpcErrorException.MethodNotFound)
        {
            throw new UsageProviderException("This Grok CLI version does not expose billing; run `grok update`.", ex);
        }

        return GrokBillingParser.Parse(billing, DateTimeOffset.Now);
    }
}
