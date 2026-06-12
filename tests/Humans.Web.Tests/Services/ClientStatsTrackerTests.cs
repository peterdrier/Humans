using AwesomeAssertions;
using Humans.Infrastructure.Services;
using Xunit;

namespace Humans.Web.Tests.Services;

public class ClientStatsTrackerTests
{
    private const string WinChrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private const string IPhone = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.1 Mobile/15E148 Safari/604.1";
    private const string Googlebot = "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)";

    [HumansFact]
    public void RecordPageView_TalliesBotsByName()
    {
        var tracker = new ClientStatsTracker();

        tracker.RecordPageView(Googlebot);
        tracker.RecordPageView(Googlebot);
        tracker.RecordPageView(WinChrome);

        var snap = tracker.GetSnapshot();
        // Only the crawler is broken out by name; human traffic stays out of the bot tally.
        snap.Bots.Should().ContainSingle().Which.Count.Should().Be(2);
        snap.Bots[0].Label.Should().NotBe("Bot");
        snap.Bots.Should().NotContain(b => b.Label == "Windows" || b.Label == "Chrome");
    }

    [HumansFact]
    public void RecordPageView_TalliesOsAndDevice()
    {
        var tracker = new ClientStatsTracker();

        tracker.RecordPageView(WinChrome);
        tracker.RecordPageView(WinChrome);
        tracker.RecordPageView(IPhone);

        var snap = tracker.GetSnapshot();
        snap.TotalPageViews.Should().Be(3);
        snap.OperatingSystems.Should().ContainSingle(c => c.Label == "Windows").Which.Count.Should().Be(2);
        snap.OperatingSystems.Should().ContainSingle(c => c.Label == "iOS").Which.Count.Should().Be(1);
        snap.DeviceTypes.Should().ContainSingle(c => c.Label == "Desktop").Which.Count.Should().Be(2);
        snap.DeviceTypes.Should().ContainSingle(c => c.Label == "Mobile").Which.Count.Should().Be(1);
    }

    [HumansFact]
    public void GetSnapshot_RanksByCountDescending()
    {
        var tracker = new ClientStatsTracker();

        tracker.RecordPageView(WinChrome);
        tracker.RecordPageView(IPhone);
        tracker.RecordPageView(IPhone);

        tracker.GetSnapshot().OperatingSystems[0].Label.Should().Be("iOS");
    }

    [HumansFact]
    public void RecordResolution_ValidSample_IsBucketed()
    {
        var tracker = new ClientStatsTracker();

        tracker.RecordResolution(1920, 1080);
        tracker.RecordResolution(1920, 1080);

        var snap = tracker.GetSnapshot();
        snap.TotalResolutionSamples.Should().Be(2);
        snap.Resolutions.Should().ContainSingle(c => c.Label == "1920x1080").Which.Count.Should().Be(2);
    }

    [HumansTheory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    [InlineData(-1, 1080)]
    [InlineData(20000, 1080)]
    public void RecordResolution_ImplausibleValues_AreIgnored(int width, int height)
    {
        var tracker = new ClientStatsTracker();

        tracker.RecordResolution(width, height);

        var snap = tracker.GetSnapshot();
        snap.TotalResolutionSamples.Should().Be(0);
        snap.Resolutions.Should().BeEmpty();
    }

    private static Humans.Application.Interfaces.ClientErrorEntry Error(
        int status = 404, string url = "/missing", string ua = WinChrome)
        => new(NodaTime.SystemClock.Instance.GetCurrentInstant(), status, "GET", url, "203.0.113.7", null, ua);

    [HumansFact]
    public void RecordError_KeepsNewestThousand_AndLifetimeCountsSurviveEviction()
    {
        var tracker = new ClientStatsTracker();

        for (var i = 0; i < 1050; i++)
            tracker.RecordError(Error(url: $"/missing/{i}"));

        var snap = tracker.GetErrorsSnapshot(1000);
        snap.TotalErrors.Should().Be(1050);
        snap.LifetimeCounts[404].Should().Be(1050);
        snap.Recent.Should().HaveCount(1000);
        // Newest first; the 50 oldest entries were evicted.
        snap.Recent[0].Url.Should().Be("/missing/1049");
        snap.Recent[^1].Url.Should().Be("/missing/50");
    }

    [HumansFact]
    public void GetErrorsSnapshot_RespectsCount()
    {
        var tracker = new ClientStatsTracker();

        for (var i = 0; i < 10; i++)
            tracker.RecordError(Error(url: $"/missing/{i}"));

        var snap = tracker.GetErrorsSnapshot(3);
        snap.Recent.Should().HaveCount(3);
        snap.Recent[0].Url.Should().Be("/missing/9");
        snap.TotalErrors.Should().Be(10);
    }

    [HumansFact]
    public void RecordError_TruncatesUrlAndUserAgent()
    {
        var tracker = new ClientStatsTracker();

        tracker.RecordError(Error(url: new string('u', 500), ua: new string('a', 500)));

        var entry = tracker.GetErrorsSnapshot(1).Recent[0];
        entry.Url.Should().HaveLength(200);
        entry.UserAgent.Should().HaveLength(150);
    }

    [HumansFact]
    public void RecordError_DerivesClientLabel()
    {
        var tracker = new ClientStatsTracker();

        tracker.RecordError(Error(ua: WinChrome));
        tracker.RecordError(Error(ua: Googlebot));

        var snap = tracker.GetErrorsSnapshot(2);
        snap.Recent[1].ClientLabel.Should().Be("Chrome · Windows");
        // Bots get the crawler name, not the collapsed "Bot" bucket.
        snap.Recent[0].ClientLabel.Should().NotBeEmpty().And.NotBe("Bot · Bot");
    }

    [HumansFact]
    public void RecordResolution_BeyondCap_FoldsIntoOther()
    {
        var tracker = new ClientStatsTracker();

        for (var i = 0; i < 250; i++)
            tracker.RecordResolution(1000 + i, 800);

        var snap = tracker.GetSnapshot();
        snap.TotalResolutionSamples.Should().Be(250);
        snap.Resolutions.Count.Should().BeLessThanOrEqualTo(201); // 200 distinct buckets + "Other"
        snap.Resolutions.Should().Contain(c => c.Label == "Other");
    }
}
