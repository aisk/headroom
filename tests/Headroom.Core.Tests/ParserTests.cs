using System.Text.Json.Nodes;
using Headroom.Core.Providers.Antigravity;
using Headroom.Core.Providers.Claude;
using Headroom.Core.Providers.Codex;
using Headroom.Core.Providers.Grok;
using Headroom.Core.Usage;

namespace Headroom.Core.Tests;

public class AntigravityUsageParserTests
{
    private const string Report = """
        {"conversation_id":"","status":"SUCCESS","response":"...","duration_seconds":0,"num_turns":0,
         "usage":{"input_tokens":0,"output_tokens":0,"total_tokens":0},
         "command":{"name":"usage","data":{"groups":[
           {"name":"Gemini Models","buckets":[
             {"id":"gemini-weekly","name":"Weekly Limit Remaining","window":"weekly","remaining_fraction":0.9985269904136658,"reset_time":"2026-11-07T15:15:30Z"},
             {"id":"gemini-5h","name":"Five Hour Limit Remaining","window":"5h","remaining_fraction":1,"reset_time":"2026-11-02T17:27:31Z"}]},
           {"name":"Claude and GPT models","buckets":[
             {"id":"3p-weekly","name":"Weekly Limit Remaining","window":"weekly","remaining_fraction":0.859639585018158,"reset_time":"2026-11-05T15:58:25Z"}]}]}}}
        """;

    [Fact]
    public void ParsesGroupedBuckets()
    {
        var snapshot = AntigravityUsageParser.Parse(Report, DateTimeOffset.UnixEpoch);

        Assert.Equal(3, snapshot.Windows.Count);
        var weekly = snapshot.Windows[2];
        Assert.Equal("Claude and GPT models", weekly.Group);
        Assert.Equal("Weekly", weekly.Label);
        Assert.Equal(86, weekly.RemainingPercent, precision: 0);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 15, 58, 25, TimeSpan.Zero), weekly.ResetsAt);
        Assert.Equal("5-hour", snapshot.Windows[1].Label);
        Assert.Same(weekly, snapshot.MostConstrained);
    }

    [Fact]
    public void FailedStatusIsReported()
    {
        var ex = Assert.Throws<UsageProviderException>(() =>
            AntigravityUsageParser.Parse("""{"status":"ERROR","error":"sign in required"}""", DateTimeOffset.UnixEpoch));
        Assert.Equal(UsageErrorKind.SignInRequired, ex.Kind);
    }

    [Fact]
    public void ReportWithoutUsageCommandIsRejected()
    {
        Assert.Throws<UsageProviderException>(() =>
            AntigravityUsageParser.Parse("""{"status":"SUCCESS","response":"Hello!"}""", DateTimeOffset.UnixEpoch));
    }
}

public class CodexRateLimitParserTests
{
    [Fact]
    public void ParsesPrimaryAndSecondaryWindows()
    {
        var limits = JsonNode.Parse("""
            {"rateLimits":{"limitId":"codex","primary":{"usedPercent":12,"windowDurationMins":300,"resetsAt":1793640172},
              "secondary":{"usedPercent":41,"windowDurationMins":10080,"resetsAt":1793778113},
              "credits":{"hasCredits":false,"unlimited":false,"balance":"0"},"planType":"plus"},
             "rateLimitsByLimitId":{"codex":{"limitId":"codex"},
              "gpt-6":{"limitId":"gpt-6","limitName":"GPT-6","primary":{"usedPercent":50,"windowDurationMins":300}}}}
            """);
        var account = JsonNode.Parse("""{"account":{"type":"chatgpt","email":"me@example.com","planType":"plus"}}""");

        var snapshot = CodexRateLimitParser.Parse(limits, account, DateTimeOffset.UnixEpoch);

        Assert.Equal(["5-hour", "Weekly", "5-hour"], snapshot.Windows.Select(w => w.Label));
        Assert.Equal(41, snapshot.Windows[1].UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793778113), snapshot.Windows[1].ResetsAt);
        Assert.Equal("GPT-6", snapshot.Windows[2].Group);
        Assert.Equal("Plus", snapshot.Plan);
        Assert.Equal("me@example.com", snapshot.Account);
        Assert.Null(snapshot.Note);
    }
}

public class GrokBillingParserTests
{
    [Fact]
    public void FreePlanWithoutPercentageReportsNoWindows()
    {
        var result = JsonNode.Parse("""
            {"config":{"currentPeriod":{"type":"USAGE_PERIOD_TYPE_WEEKLY","start":"2026-10-29T00:00:00+00:00","end":"2026-11-05T00:00:00+00:00"},
              "onDemandCap":{"val":0},"onDemandUsed":{"val":0},"prepaidBalance":{"val":0},"isUnifiedBillingUser":true},
             "subscription_tier":"Free"}
            """);

        var snapshot = GrokBillingParser.Parse(result, DateTimeOffset.UnixEpoch);

        Assert.Empty(snapshot.Windows);
        Assert.Equal("Free", snapshot.Plan);
        Assert.Contains("does not report a usage percentage", snapshot.Note);
    }

    [Fact]
    public void UsesCreditPercentageAndPrepaidBalance()
    {
        var result = JsonNode.Parse("""
            {"config":{"creditUsagePercent":37.5,"currentPeriod":{"type":"USAGE_PERIOD_TYPE_WEEKLY","end":"2026-11-05T00:00:00Z"},
              "prepaidBalance":{"val":"1250"}},"subscription_tier":"SuperGrok"}
            """);

        var snapshot = GrokBillingParser.Parse(result, DateTimeOffset.UnixEpoch);

        var window = Assert.Single(snapshot.Windows);
        Assert.Equal("Weekly", window.Label);
        Assert.Equal(37.5, window.UsedPercent);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero), window.ResetsAt);
        Assert.Equal("Prepaid balance: $12.50", snapshot.Note);
    }

    [Fact]
    public void FallsBackToOnDemandSpend()
    {
        var result = JsonNode.Parse("""{"config":{"onDemandCap":{"val":2000},"onDemandUsed":{"val":500}}}""");

        var snapshot = GrokBillingParser.Parse(result, DateTimeOffset.UnixEpoch);

        Assert.Equal(25, Assert.Single(snapshot.Windows).UsedPercent);
    }
}

public class ClaudeUsageParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 21, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public void ParsesInlineReport()
    {
        const string output = """
            You are currently using your subscription to power your Claude Code usage

            Current session: 23% used · resets 11pm (Asia/Tokyo)
            Current week (all models): 7% used · resets Nov 5, 7pm (Asia/Tokyo)
            Current week (Fable): 0% used · resets Nov 5, 7pm (Asia/Tokyo)

            What's contributing to your usage?
            Last 24h · 44 requests · 4 sessions
              54%  Turn off skills you don't use · 11k tokens per request
            """;

        var snapshot = ClaudeUsageParser.Parse(output, Now);

        Assert.Equal(3, snapshot.Windows.Count);
        Assert.Equal(("Session", (string?)null, 23d), (snapshot.Windows[0].Label, snapshot.Windows[0].Group, snapshot.Windows[0].UsedPercent));
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 23, 0, 0, TimeSpan.FromHours(9)), snapshot.Windows[0].ResetsAt);
        Assert.Equal("All models", snapshot.Windows[1].Group);
        Assert.Equal("Weekly", snapshot.Windows[1].Label);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 19, 0, 0, TimeSpan.FromHours(9)), snapshot.Windows[1].ResetsAt);
        Assert.Equal("Fable", snapshot.Windows[2].Group);
    }

    [Fact]
    public void ParsesPanelReportWithAnsi()
    {
        const string output = "\u001b[1mCurrent session\u001b[0m\n  ████▌     9% used\n  Resets 10:30pm (Asia/Tokyo)\n\n" +
                              "Current week (all models)\n  ██  60% left\n  Resets Nov 5 at 7pm (Asia/Tokyo)\n";

        var snapshot = ClaudeUsageParser.Parse(output, Now);

        Assert.Equal(2, snapshot.Windows.Count);
        Assert.Equal(9, snapshot.Windows[0].UsedPercent);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 22, 30, 0, TimeSpan.FromHours(9)), snapshot.Windows[0].ResetsAt);
        Assert.Equal(40, snapshot.Windows[1].UsedPercent);
    }

    [Fact]
    public void TimeEarlierThanNowRollsToTomorrow()
    {
        var reset = ClaudeResetParser.TryParse("resets 8pm (Asia/Tokyo)", Now);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 20, 0, 0, TimeSpan.FromHours(9)), reset);
    }

    [Fact]
    public void ErrorsAreSurfaced()
    {
        var ex = Assert.Throws<UsageProviderException>(() =>
            ClaudeUsageParser.Parse("Failed to load usage data: 401", Now));
        Assert.Contains("could not load usage data", ex.Message);
    }

    [Fact]
    public void InsightPercentagesAreNotQuotas()
    {
        Assert.Throws<UsageProviderException>(() =>
            ClaudeUsageParser.Parse("What's contributing to your usage?\n  54% used by skills", Now));
    }
}
