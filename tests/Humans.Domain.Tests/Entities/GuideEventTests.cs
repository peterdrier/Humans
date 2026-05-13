using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Testing;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace Humans.Domain.Tests.Entities;

public class GuideEventTests
{
    private readonly FakeClock _clock;

    public GuideEventTests()
    {
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 18, 12, 0));
    }

    [HumansTheory]
    [InlineData(GuideEventStatus.Draft)]
    [InlineData(GuideEventStatus.Rejected)]
    [InlineData(GuideEventStatus.ResubmitRequested)]
    public void Submit_FromValidState_SetsPendingAndTimestamps(GuideEventStatus source)
    {
        var guideEvent = CreateEvent(source);

        guideEvent.Submit(_clock);

        guideEvent.Status.Should().Be(GuideEventStatus.Pending);
        guideEvent.SubmittedAt.Should().Be(_clock.GetCurrentInstant());
        guideEvent.LastUpdatedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [HumansFact]
    public void Submit_FromWithdrawn_Throws()
    {
        var guideEvent = CreateEvent(GuideEventStatus.Withdrawn);

        var action = () => guideEvent.Submit(_clock);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("Cannot submit event in Withdrawn state");
    }

    [HumansTheory]
    [InlineData(GuideEventStatus.Approved)]
    [InlineData(GuideEventStatus.Pending)]
    public void Submit_FromInvalidState_Throws(GuideEventStatus source)
    {
        var guideEvent = CreateEvent(source);

        var action = () => guideEvent.Submit(_clock);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage($"Cannot submit event in {source} state");
    }

    [HumansTheory]
    [InlineData(GuideEventStatus.Draft)]
    [InlineData(GuideEventStatus.Pending)]
    public void Withdraw_FromValidState_SetsWithdrawnAndLastUpdated(GuideEventStatus source)
    {
        var guideEvent = CreateEvent(source);

        guideEvent.Withdraw(_clock);

        guideEvent.Status.Should().Be(GuideEventStatus.Withdrawn);
        guideEvent.LastUpdatedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [HumansTheory]
    [InlineData(GuideEventStatus.Approved)]
    [InlineData(GuideEventStatus.Rejected)]
    [InlineData(GuideEventStatus.ResubmitRequested)]
    [InlineData(GuideEventStatus.Withdrawn)]
    public void Withdraw_FromInvalidState_Throws(GuideEventStatus source)
    {
        var guideEvent = CreateEvent(source);

        var action = () => guideEvent.Withdraw(_clock);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage($"Cannot withdraw event in {source} state");
    }

    [HumansTheory]
    [InlineData(ModerationActionType.Approved, GuideEventStatus.Approved)]
    [InlineData(ModerationActionType.Rejected, GuideEventStatus.Rejected)]
    [InlineData(ModerationActionType.ResubmitRequested, GuideEventStatus.ResubmitRequested)]
    public void ApplyModerationAction_FromPending_TransitionsToExpectedStatus(
        ModerationActionType action,
        GuideEventStatus expectedStatus)
    {
        var guideEvent = CreateEvent(GuideEventStatus.Pending);

        guideEvent.ApplyModerationAction(action, _clock);

        guideEvent.Status.Should().Be(expectedStatus);
        guideEvent.LastUpdatedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [HumansTheory]
    [InlineData(GuideEventStatus.Draft)]
    [InlineData(GuideEventStatus.Approved)]
    [InlineData(GuideEventStatus.Rejected)]
    [InlineData(GuideEventStatus.ResubmitRequested)]
    [InlineData(GuideEventStatus.Withdrawn)]
    public void ApplyModerationAction_FromInvalidState_Throws(GuideEventStatus source)
    {
        var guideEvent = CreateEvent(source);

        var action = () => guideEvent.ApplyModerationAction(ModerationActionType.Approved, _clock);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage($"Cannot moderate event in {source} state");
    }

    [HumansFact]
    public void ApplyModerationAction_WithUnknownAction_Throws()
    {
        var guideEvent = CreateEvent(GuideEventStatus.Pending);

        var action = () => guideEvent.ApplyModerationAction((ModerationActionType)999, _clock);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [HumansFact]
    public void GetOccurrenceInstants_ForRecurringEvent_ReturnsExpandedInstants()
    {
        var guideEvent = CreateEvent(GuideEventStatus.Draft);
        guideEvent.IsRecurring = true;
        guideEvent.RecurrenceDays = "0,2,4";

        var occurrences = guideEvent.GetOccurrenceInstants();

        occurrences.Should().HaveCount(3);
        occurrences[0].Should().Be(guideEvent.StartAt);
        occurrences[1].Should().Be(guideEvent.StartAt.Plus(Duration.FromDays(2)));
        occurrences[2].Should().Be(guideEvent.StartAt.Plus(Duration.FromDays(4)));
    }

    private GuideEvent CreateEvent(GuideEventStatus status)
    {
        return new GuideEvent
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
