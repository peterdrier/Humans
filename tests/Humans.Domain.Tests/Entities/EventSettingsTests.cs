using AwesomeAssertions;
using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Domain.Tests.Entities;

/// <summary>
/// Covers <see cref="EventSettings.IsEarlyEntryClosed"/> — the single home for the
/// early-entry-closed clock rule. Both the server gates (ShiftSignupService) and the
/// browse/onboarding UI call this, ANDing it with the viewer's privilege and the
/// shift's <see cref="Shift.IsEarlyEntry"/> at each call site.
/// </summary>
public class EventSettingsTests
{
    private static readonly Instant Close = Instant.FromUtc(2026, 6, 15, 12, 0);

    private static EventSettings EventWithClose(Instant? close) =>
        new() { Id = Guid.NewGuid(), EarlyEntryClose = close };

    [HumansFact]
    public void IsEarlyEntryClosed_NoCloseConfigured_ReturnsFalse()
    {
        EventWithClose(null).IsEarlyEntryClosed(Close.Plus(Duration.FromDays(1)))
            .Should().BeFalse();
    }

    [HumansFact]
    public void IsEarlyEntryClosed_BeforeClose_ReturnsFalse()
    {
        EventWithClose(Close).IsEarlyEntryClosed(Close.Minus(Duration.FromHours(1)))
            .Should().BeFalse();
    }

    [HumansFact]
    public void IsEarlyEntryClosed_AtCloseInstant_ReturnsTrue()
    {
        // Boundary: the instant of close itself counts as closed (>=).
        EventWithClose(Close).IsEarlyEntryClosed(Close).Should().BeTrue();
    }

    [HumansFact]
    public void IsEarlyEntryClosed_AfterClose_ReturnsTrue()
    {
        EventWithClose(Close).IsEarlyEntryClosed(Close.Plus(Duration.FromHours(1)))
            .Should().BeTrue();
    }

    // IsEarlyEntrySignupsClosedFor — the page-level eligibility the browse/onboarding
    // UI surfaces (was a controller helper; pushed to the domain per
    // memory/architecture/no-business-logic-in-controllers.md).

    [HumansFact]
    public void IsEarlyEntrySignupsClosedFor_NonPrivileged_AfterClose_ReturnsTrue()
    {
        EventWithClose(Close).IsEarlyEntrySignupsClosedFor(isPrivileged: false, Close.Plus(Duration.FromHours(1)))
            .Should().BeTrue();
    }

    [HumansFact]
    public void IsEarlyEntrySignupsClosedFor_NonPrivileged_AtCloseInstant_ReturnsTrue()
    {
        // Boundary mirrors the server gate: the close instant itself locks (>=).
        EventWithClose(Close).IsEarlyEntrySignupsClosedFor(isPrivileged: false, Close)
            .Should().BeTrue();
    }

    [HumansFact]
    public void IsEarlyEntrySignupsClosedFor_NonPrivileged_BeforeClose_ReturnsFalse()
    {
        EventWithClose(Close).IsEarlyEntrySignupsClosedFor(isPrivileged: false, Close.Minus(Duration.FromHours(1)))
            .Should().BeFalse();
    }

    [HumansFact]
    public void IsEarlyEntrySignupsClosedFor_Privileged_AfterClose_ReturnsFalse()
    {
        // Admins/coordinators bypass the lock exactly as the server gate does.
        EventWithClose(Close).IsEarlyEntrySignupsClosedFor(isPrivileged: true, Close.Plus(Duration.FromHours(1)))
            .Should().BeFalse();
    }

    [HumansFact]
    public void IsEarlyEntrySignupsClosedFor_NoCloseConfigured_ReturnsFalse()
    {
        // Privilege OR an unset close short-circuits to false regardless of the clock.
        EventWithClose(null).IsEarlyEntrySignupsClosedFor(isPrivileged: false, Close.Plus(Duration.FromDays(1)))
            .Should().BeFalse();
        EventWithClose(null).IsEarlyEntrySignupsClosedFor(isPrivileged: true, Close.Plus(Duration.FromDays(1)))
            .Should().BeFalse();
    }
}
