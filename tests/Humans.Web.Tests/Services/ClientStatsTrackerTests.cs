using AwesomeAssertions;
using Humans.Infrastructure.Services;
using Humans.Testing;
using Xunit;

namespace Humans.Web.Tests.Services;

public class ClientStatsTrackerTests
{
    private const string WinChrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private const string IPhone = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.1 Mobile/15E148 Safari/604.1";

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
