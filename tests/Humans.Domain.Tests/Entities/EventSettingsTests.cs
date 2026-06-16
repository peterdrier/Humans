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
}
