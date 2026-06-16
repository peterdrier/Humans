using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Web.Controllers;
using NodaTime;

namespace Humans.Web.Tests.Controllers;

/// <summary>
/// Covers <see cref="ShiftsController.IsEarlyEntrySignupsClosed"/> — the page-level
/// flag the rota toggles read to lock build-shift Sign-Up after EarlyEntryClose.
/// The row partials further AND it with <c>Shift.IsEarlyEntry</c> (tested via the
/// view), and the server gate in ShiftSignupService enforces it for real; this
/// isolates the privilege/clock boundary that decides whether the lock applies.
/// </summary>
public class ShiftsControllerEarlyEntryGateTests
{
    private static readonly Instant Close = Instant.FromUtc(2026, 6, 15, 12, 0);

    private static EventSettings EventWithClose(Instant? close) =>
        new() { Id = Guid.NewGuid(), EarlyEntryClose = close };

    [HumansFact]
    public void NoCloseConfigured_NotClosed()
    {
        ShiftsController.IsEarlyEntrySignupsClosed(EventWithClose(null), isPrivileged: false, Close.Plus(Duration.FromDays(1)))
            .Should().BeFalse();
    }

    [HumansFact]
    public void BeforeClose_NotClosed()
    {
        ShiftsController.IsEarlyEntrySignupsClosed(EventWithClose(Close), isPrivileged: false, Close.Minus(Duration.FromHours(1)))
            .Should().BeFalse();
    }

    [HumansFact]
    public void AtOrAfterClose_NonPrivileged_Closed()
    {
        // Boundary: the instant of close itself locks (>=), matching the server gate.
        ShiftsController.IsEarlyEntrySignupsClosed(EventWithClose(Close), isPrivileged: false, Close)
            .Should().BeTrue();
        ShiftsController.IsEarlyEntrySignupsClosed(EventWithClose(Close), isPrivileged: false, Close.Plus(Duration.FromHours(1)))
            .Should().BeTrue();
    }

    [HumansFact]
    public void AfterClose_Privileged_NotClosed()
    {
        // Admins/coordinators bypass the lock exactly as the server gate does.
        ShiftsController.IsEarlyEntrySignupsClosed(EventWithClose(Close), isPrivileged: true, Close.Plus(Duration.FromHours(1)))
            .Should().BeFalse();
    }
}
