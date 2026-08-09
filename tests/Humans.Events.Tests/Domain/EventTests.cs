using AwesomeAssertions;
using Humans.Events.Contracts;
using Humans.Events.Domain;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace Humans.Events.Tests;

public sealed class EventTests
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

    [HumansFact]
    public void Withdraw_CampEventInDraft_Throws()
    {
        // Camp (barrio) events have no Draft stage — the asymmetry the
        // controllers used to encode is now owned by the entity (E2-E4).
        var guideEvent = CreateEvent(EventStatus.Draft);
        guideEvent.CampId = Guid.NewGuid();

        var action = () => guideEvent.Withdraw(_clock);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("Cannot withdraw event in Draft state");
    }

    [HumansTheory]
    [InlineData(EventStatus.Draft, false, true)]
    [InlineData(EventStatus.Draft, true, false)]   // camp events have no Draft stage
    [InlineData(EventStatus.Pending, false, true)]
    [InlineData(EventStatus.Pending, true, true)]
    [InlineData(EventStatus.Rejected, false, true)]
    [InlineData(EventStatus.Rejected, true, true)]
    [InlineData(EventStatus.ResubmitRequested, false, true)]
    [InlineData(EventStatus.ResubmitRequested, true, true)]
    [InlineData(EventStatus.Approved, false, false)]
    [InlineData(EventStatus.Withdrawn, false, false)]
    public void IsEditableBySubmitter_CoversStatusAndCampAsymmetry(
        EventStatus status, bool isCampEvent, bool expected)
    {
        Event.IsEditableBySubmitter(status, isCampEvent).Should().Be(expected);
    }

    [HumansTheory]
    [InlineData(EventStatus.Draft, false, true)]
    [InlineData(EventStatus.Draft, true, false)]   // camp events have no Draft stage
    [InlineData(EventStatus.Pending, false, true)]
    [InlineData(EventStatus.Pending, true, true)]
    [InlineData(EventStatus.Approved, false, true)]
    [InlineData(EventStatus.Approved, true, true)]
    [InlineData(EventStatus.Rejected, false, false)]
    [InlineData(EventStatus.Withdrawn, false, false)]
    public void IsWithdrawableBySubmitter_CoversStatusAndCampAsymmetry(
        EventStatus status, bool isCampEvent, bool expected)
    {
        Event.IsWithdrawableBySubmitter(status, isCampEvent).Should().Be(expected);
    }

    [HumansTheory]
    [InlineData(true, 90, 1440)]
    [InlineData(false, 90, 90)]
    public void ResolveAllDaySchedule_AllDayMeansMidnightStartFullDay(
        bool isAllDay, int requestedMinutes, int expectedMinutes)
    {
        var (startTime, durationMinutes) = Event.ResolveAllDaySchedule(
            isAllDay, new TimeSpan(14, 30, 0), requestedMinutes);

        durationMinutes.Should().Be(expectedMinutes);
        startTime.Should().Be(isAllDay ? TimeSpan.Zero : new TimeSpan(14, 30, 0));
    }

    // Three facts rather than one theory: EventModerationActionType is internal to the
    // section (only EventStatus is on the Contracts surface), and xUnit requires a public
    // test class, so the enum cannot appear in a test method's signature. Naming it in the
    // body keeps the InternalsVisibleTo access without widening the section's surface.
    [HumansFact]
    public void ApplyModerationAction_Approved_FromPending_TransitionsToApproved() =>
        AssertModerationTransition(EventModerationActionType.Approved, EventStatus.Approved);

    [HumansFact]
    public void ApplyModerationAction_Rejected_FromPending_TransitionsToRejected() =>
        AssertModerationTransition(EventModerationActionType.Rejected, EventStatus.Rejected);

    [HumansFact]
    public void ApplyModerationAction_ResubmitRequested_FromPending_TransitionsToResubmitRequested() =>
        AssertModerationTransition(
            EventModerationActionType.ResubmitRequested, EventStatus.ResubmitRequested);

    private void AssertModerationTransition(
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

    [HumansFact]
    public void ApplyIndividualEdit_SetsFieldsAndClearsRecurrenceDaysWhenNotRecurring()
    {
        var guideEvent = CreateEvent(EventStatus.Pending);
        var categoryId = Guid.NewGuid();
        var venueId = Guid.NewGuid();
        var startAt = Instant.FromUtc(2026, 7, 2, 14, 0);

        guideEvent.ApplyIndividualEdit(
            categoryId, venueId, "New title", "New description",
            startAt, 90, "Near the fire", "Host name",
            isRecurring: false, recurrenceDays: "1,2");

        guideEvent.CategoryId.Should().Be(categoryId);
        guideEvent.GuideSharedVenueId.Should().Be(venueId);
        guideEvent.Title.Should().Be("New title");
        guideEvent.Description.Should().Be("New description");
        guideEvent.StartAt.Should().Be(startAt);
        guideEvent.DurationMinutes.Should().Be(90);
        guideEvent.LocationNote.Should().Be("Near the fire");
        guideEvent.Host.Should().Be("Host name");
        guideEvent.IsRecurring.Should().BeFalse();
        guideEvent.RecurrenceDays.Should().BeNull(); // cleared when not recurring
    }

    [HumansFact]
    public void ApplyIndividualEdit_Recurring_KeepsRecurrenceDays()
    {
        var guideEvent = CreateEvent(EventStatus.Pending);

        guideEvent.ApplyIndividualEdit(
            Guid.NewGuid(), Guid.NewGuid(), "Title", "Description",
            Instant.FromUtc(2026, 7, 2, 14, 0), 60, null, null,
            isRecurring: true, recurrenceDays: "0,2,4");

        guideEvent.IsRecurring.Should().BeTrue();
        guideEvent.RecurrenceDays.Should().Be("0,2,4");
    }

    [HumansFact]
    public void ApplyBarrioEdit_SetsFieldsIncludingPriorityRankAndClearsRecurrenceDaysWhenNotRecurring()
    {
        var guideEvent = CreateEvent(EventStatus.Pending);
        guideEvent.CampId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var startAt = Instant.FromUtc(2026, 7, 3, 9, 0);

        guideEvent.ApplyBarrioEdit(
            categoryId, "Barrio title", "Barrio description",
            startAt, 45, "Behind the stage", "Camp host",
            isRecurring: false, recurrenceDays: "1", priorityRank: 3);

        guideEvent.CategoryId.Should().Be(categoryId);
        guideEvent.Title.Should().Be("Barrio title");
        guideEvent.Description.Should().Be("Barrio description");
        guideEvent.StartAt.Should().Be(startAt);
        guideEvent.DurationMinutes.Should().Be(45);
        guideEvent.LocationNote.Should().Be("Behind the stage");
        guideEvent.Host.Should().Be("Camp host");
        guideEvent.IsRecurring.Should().BeFalse();
        guideEvent.RecurrenceDays.Should().BeNull();
        guideEvent.PriorityRank.Should().Be(3);
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
