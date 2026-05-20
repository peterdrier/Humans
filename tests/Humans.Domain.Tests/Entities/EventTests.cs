using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace Humans.Domain.Tests.Entities;

public class EventTests
{
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 3, 18, 12, 0));

    [HumansTheory]
    [InlineData(EventStatus.Draft)]
    [InlineData(EventStatus.Rejected)]
    [InlineData(EventStatus.ResubmitRequested)]
    public void Submit_FromValidState_SetsPendingAndTimestamps(EventStatus source)
    {
        var guideEvent = CreateEvent(source);

        guideEvent.Submit(_clock);

        guideEvent.Status.Should().Be(EventStatus.Pending);
        guideEvent.SubmittedAt.Should().Be(_clock.GetCurrentInstant());
        guideEvent.LastUpdatedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [HumansFact]
    public void Submit_FromWithdrawn_Throws()
    {
        var guideEvent = CreateEvent(EventStatus.Withdrawn);

        var action = () => guideEvent.Submit(_clock);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("Cannot submit event in Withdrawn state");
    }

    [HumansTheory]
    [InlineData(EventStatus.Approved)]
    [InlineData(EventStatus.Pending)]
    public void Submit_FromInvalidState_Throws(EventStatus source)
    {
        var guideEvent = CreateEvent(source);

        var action = () => guideEvent.Submit(_clock);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage($"Cannot submit event in {source} state");
    }

    [HumansTheory]
    [InlineData(EventStatus.Draft)]
    [InlineData(EventStatus.Pending)]
    [InlineData(EventStatus.Approved)]
    public void Withdraw_FromValidState_SetsWithdrawnAndLastUpdated(EventStatus source)
    {
        var guideEvent = CreateEvent(source);

        guideEvent.Withdraw(_clock);

        guideEvent.Status.Should().Be(EventStatus.Withdrawn);
        guideEvent.LastUpdatedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [HumansTheory]
    [InlineData(EventStatus.Rejected)]
    [InlineData(EventStatus.ResubmitRequested)]
    [InlineData(EventStatus.Withdrawn)]
    public void Withdraw_FromInvalidState_Throws(EventStatus source)
    {
        var guideEvent = CreateEvent(source);

        var action = () => guideEvent.Withdraw(_clock);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage($"Cannot withdraw event in {source} state");
    }

    [HumansTheory]
    [InlineData(EventModerationActionType.Approved, EventStatus.Approved)]
    [InlineData(EventModerationActionType.Rejected, EventStatus.Rejected)]
    [InlineData(EventModerationActionType.ResubmitRequested, EventStatus.ResubmitRequested)]
    public void ApplyModerationAction_FromPending_TransitionsToExpectedStatus(
        EventModerationActionType action,
        EventStatus expectedStatus)
    {
        var guideEvent = CreateEvent(EventStatus.Pending);

        guideEvent.ApplyModerationAction(action, _clock);

        guideEvent.Status.Should().Be(expectedStatus);
        guideEvent.LastUpdatedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [HumansTheory]
    [InlineData(EventStatus.Draft)]
    [InlineData(EventStatus.Approved)]
    [InlineData(EventStatus.Rejected)]
    [InlineData(EventStatus.ResubmitRequested)]
    [InlineData(EventStatus.Withdrawn)]
    public void ApplyModerationAction_FromInvalidState_Throws(EventStatus source)
    {
        var guideEvent = CreateEvent(source);

        var action = () => guideEvent.ApplyModerationAction(EventModerationActionType.Approved, _clock);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage($"Cannot moderate event in {source} state");
    }

    [HumansFact]
    public void ApplyModerationAction_WithUnknownAction_Throws()
    {
        var guideEvent = CreateEvent(EventStatus.Pending);

        var action = () => guideEvent.ApplyModerationAction((EventModerationActionType)999, _clock);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [HumansFact]
    public void GetOccurrenceInstants_Recurring_UsesGateOpeningDayOffsets()
    {
        var timeZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        var gateOpeningDate = new LocalDate(2026, 7, 5);
        var guideEvent = CreateEvent(EventStatus.Approved);
        guideEvent.StartAt = new LocalDateTime(2026, 7, 12, 18, 30)
            .InZoneStrictly(timeZone)
            .ToInstant();
        guideEvent.IsRecurring = true;
        guideEvent.RecurrenceDays = "2,3";

        var occurrences = guideEvent.GetOccurrenceInstants(gateOpeningDate, timeZone);

        occurrences.Should().Equal(
            new LocalDateTime(2026, 7, 7, 18, 30).InZoneStrictly(timeZone).ToInstant(),
            new LocalDateTime(2026, 7, 8, 18, 30).InZoneStrictly(timeZone).ToInstant());
    }

    [HumansFact]
    public void GetOccurrenceInstants_NonRecurring_ReturnsStartAt()
    {
        var guideEvent = CreateEvent(EventStatus.Approved);

        var occurrences = guideEvent.GetOccurrenceInstants(new LocalDate(2026, 7, 5), DateTimeZone.Utc);

        occurrences.Should().Equal(guideEvent.StartAt);
    }

    [HumansFact]
    public void GetOccurrenceInstants_RecurringWithEmptyDays_ReturnsStartAt()
    {
        var guideEvent = CreateEvent(EventStatus.Approved);
        guideEvent.IsRecurring = true;
        guideEvent.RecurrenceDays = "";

        var occurrences = guideEvent.GetOccurrenceInstants(new LocalDate(2026, 7, 5), DateTimeZone.Utc);

        occurrences.Should().Equal(guideEvent.StartAt);
    }

    private Event CreateEvent(EventStatus status)
    {
        return new Event
        {
            Id = Guid.NewGuid(),
            CategoryId = Guid.NewGuid(),
            SubmitterUserId = Guid.NewGuid(),
            Title = "Test event",
            Description = "Test description",
            StartAt = Instant.FromUtc(2026, 7, 1, 10, 0),
            DurationMinutes = 60,
            PriorityRank = 1,
            Status = status,
            SubmittedAt = Instant.MinValue,
            LastUpdatedAt = Instant.MinValue
        };
    }
}
