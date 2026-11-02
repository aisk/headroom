using System.Text.Json.Nodes;
using Headroom.Core.Providers.Antigravity;
using Headroom.Core.Providers.Claude;
using Headroom.Core.Providers.Codex;
using Headroom.Core.Usage;

namespace Headroom.Core.Tests;

public class ClaudeSignInTests
{
    [Fact]
    public void ParsesSubscriptionLogin()
    {
        var status = ClaudeAuthStatus.TryParse("""
            {"loggedIn": true, "authMethod": "claude.ai", "apiProvider": "firstParty",
             "email": "me@example.com", "subscriptionType": "max"}
            """);

        Assert.NotNull(status);
        Assert.True(status.LoggedIn);
        Assert.True(status.IsSubscription);
        Assert.Equal("Max", status.PlanDisplayName);
        Assert.Equal("me@example.com", status.Email);
    }

    [Fact]
    public void ApiKeyLoginIsNotASubscription()
    {
        var status = ClaudeAuthStatus.TryParse("""{"loggedIn": true, "authMethod": "apiKey", "apiProvider": "firstParty"}""");
        Assert.False(status!.IsSubscription);
    }

    [Fact]
    public void SignedOutAndUnknownOutput()
    {
        Assert.False(ClaudeAuthStatus.TryParse("""{"loggedIn": false}""")!.LoggedIn);
        Assert.Null(ClaudeAuthStatus.TryParse("error: unknown command 'auth'"));
    }

    [Theory]
    [InlineData("API Error: 401 {\"type\":\"authentication_error\"} · Please run /login")]
    [InlineData("OAuth token has expired. Please run /login")]
    public void ExpiredLoginRequiresSignIn(string output)
    {
        var ex = Assert.Throws<UsageProviderException>(() => ClaudeUsageParser.Parse(output, DateTimeOffset.UnixEpoch));
        Assert.Equal(UsageErrorKind.SignInRequired, ex.Kind);
    }

    [Fact]
    public void SubscriptionNoticeWithoutQuotaIsExplained()
    {
        var ex = Assert.Throws<UsageProviderException>(() => ClaudeUsageParser.Parse(
            "You are currently using your subscription to power your Claude Code usage\n", DateTimeOffset.UnixEpoch));
        Assert.Equal(UsageErrorKind.General, ex.Kind);
        Assert.Contains("did not report session or weekly limits", ex.Message);
    }
}

public class CodexSignInTests
{
    [Fact]
    public void MissingAccountRequiresSignIn()
    {
        var ex = Assert.Throws<UsageProviderException>(() =>
            CodexAccountCheck.ThrowIfUnusable(JsonNode.Parse("""{"account":null,"requiresOpenaiAuth":true}""")));
        Assert.Equal(UsageErrorKind.SignInRequired, ex.Kind);
    }

    [Fact]
    public void ApiKeyAccountHasNoPlanLimits()
    {
        var ex = Assert.Throws<UsageProviderException>(() =>
            CodexAccountCheck.ThrowIfUnusable(JsonNode.Parse("""{"account":{"type":"apiKey"},"requiresOpenaiAuth":true}""")));
        Assert.Equal(UsageErrorKind.General, ex.Kind);
    }

    [Fact]
    public void ChatGptAccountIsUsable()
    {
        CodexAccountCheck.ThrowIfUnusable(JsonNode.Parse("""{"account":{"type":"chatgpt","email":"a@b.c"}}"""));
        Assert.True(CodexAccountCheck.IsAuthenticationFailure("unexpected status 401 Unauthorized"));
        Assert.False(CodexAccountCheck.IsAuthenticationFailure("request to port 4012 failed"));
    }
}

public class AntigravityFailureClassifierTests
{
    [Theory]
    [InlineData("Select login method:\n> Google")]
    [InlineData("You are not logged into Antigravity.")]
    public void LoginPromptRequiresSignIn(string output) =>
        Assert.Equal(UsageErrorKind.SignInRequired, AntigravityFailureClassifier.Classify(1, output).Kind);

    [Fact]
    public void NetworkFailureHidesRawOutput()
    {
        var ex = AntigravityFailureClassifier.Classify(1, "Get \"https://example.com/u/me@example.com\": dial tcp: i/o timeout");
        Assert.Contains("network request failed", ex.Message);
        Assert.DoesNotContain("example.com", ex.Message);
    }

    [Fact]
    public void UnknownFailureUsesFixedMessage() =>
        Assert.Equal("agy exited 2 without a usage report.", AntigravityFailureClassifier.Classify(2, "panic: something").Message);
}

public class UsageMonitorSignInTests
{
    private sealed class SignedOutProvider(bool pause) : IUsageProvider
    {
        public int Calls { get; private set; }

        public string Id => "test";

        public string DisplayName => "Test";

        public bool PauseAutoRefreshWhenSignedOut => pause;

        public bool IsInstalled() => true;

        public Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken)
        {
            Calls++;
            throw new UsageProviderException("signed out", UsageErrorKind.SignInRequired);
        }
    }

    [Fact]
    public async Task SignInFailureClearsSnapshotAndPausesOnlyWhenRequested()
    {
        var paused = new SignedOutProvider(pause: true);
        var polled = new SignedOutProvider(pause: false);
        using var monitor = new UsageMonitor([paused, polled]);

        await monitor.RefreshAllAsync();
        await monitor.RefreshAllAsync();
        Assert.Equal(1, paused.Calls);
        Assert.Equal(2, polled.Calls);
        Assert.True(monitor.Providers[0].IsAutoRefreshPaused);
        Assert.Null(monitor.Providers[0].Snapshot);

        await monitor.RefreshAllAsync(manual: true);
        Assert.Equal(2, paused.Calls);
    }
}
