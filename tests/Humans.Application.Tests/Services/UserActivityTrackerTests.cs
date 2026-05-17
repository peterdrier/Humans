using AwesomeAssertions;
using Humans.Infrastructure.Services;
using NodaTime;
using NodaTime.Testing;

namespace Humans.Application.Tests.Services;

public class UserActivityTrackerTests
{
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 5, 18, 12, 0));

    [HumansFact]
    public void Touch_RecordsUser_CountedWithinWindow()
    {
        var tracker = new UserActivityTracker(_clock);

        tracker.Touch(Guid.NewGuid());

        tracker.CountActiveWithin(Duration.FromMinutes(5)).Should().Be(1);
    }

    [HumansFact]
    public void Touch_SameUserTwice_CountsOnce()
    {
        var tracker = new UserActivityTracker(_clock);
        var user = Guid.NewGuid();

        tracker.Touch(user);
        _clock.AdvanceSeconds(10);
        tracker.Touch(user);

        tracker.CountActiveWithin(Duration.FromMinutes(5)).Should().Be(1);
    }

    [HumansFact]
    public void CountActiveWithin_ExcludesUsersOutsideWindow()
    {
        var tracker = new UserActivityTracker(_clock);
        var stale = Guid.NewGuid();
        var recent = Guid.NewGuid();

        tracker.Touch(stale);
        _clock.AdvanceSeconds(60 * 10); // 10 min later
        tracker.Touch(recent);

        tracker.CountActiveWithin(Duration.FromMinutes(5)).Should().Be(1);
        tracker.CountActiveWithin(Duration.FromMinutes(15)).Should().Be(2);
    }

    [HumansFact]
    public void CountActiveWithin_OnEmptyTracker_ReturnsZero()
    {
        var tracker = new UserActivityTracker(_clock);

        tracker.CountActiveWithin(Duration.FromHours(24)).Should().Be(0);
    }

    [HumansFact]
    public void Touch_RefreshingStaleUser_BringsBackIntoWindow()
    {
        var tracker = new UserActivityTracker(_clock);
        var user = Guid.NewGuid();

        tracker.Touch(user);
        _clock.AdvanceSeconds(60 * 10); // 10 min later — outside 5m window
        tracker.CountActiveWithin(Duration.FromMinutes(5)).Should().Be(0);

        tracker.Touch(user);
        tracker.CountActiveWithin(Duration.FromMinutes(5)).Should().Be(1);
    }
}
