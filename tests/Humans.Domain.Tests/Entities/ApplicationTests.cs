using AwesomeAssertions;
using NodaTime;
using NodaTime.Testing;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Xunit;

namespace Humans.Domain.Tests.Entities;

public class ApplicationTests
{
    private readonly FakeClock _clock;

    public ApplicationTests()
    {
        _clock = new FakeClock(Instant.FromUtc(2024, 1, 15, 10, 0));
    }

    [Fact]
    public void NewApplication_ShouldHaveSubmittedStatus()
    {
        var application = new Application
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Motivation = "I want to join",
            SubmittedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };

        application.Status.Should().Be(ApplicationStatus.Submitted);
    }

    [Fact]
    public void Approve_ShouldTransitionToApproved()
    {
        var reviewerId = Guid.NewGuid();
        var application = CreateSubmittedApplication();

        application.Approve(reviewerId, "Welcome!", _clock);

        application.Status.Should().Be(ApplicationStatus.Approved);
        application.ReviewNotes.Should().Be("Welcome!");
        application.ResolvedAt.Should().NotBeNull();
    }

    [Fact]
    public void Reject_ShouldTransitionToRejected()
    {
        var reviewerId = Guid.NewGuid();
        var application = CreateSubmittedApplication();

        application.Reject(reviewerId, "Does not meet criteria", _clock);

        application.Status.Should().Be(ApplicationStatus.Rejected);
        application.ReviewNotes.Should().Be("Does not meet criteria");
        application.ResolvedAt.Should().NotBeNull();
    }

    [Fact]
    public void Withdraw_FromSubmitted_ShouldTransitionToWithdrawn()
    {
        var application = CreateSubmittedApplication();

        application.Withdraw(_clock);

        application.Status.Should().Be(ApplicationStatus.Withdrawn);
        application.ResolvedAt.Should().NotBeNull();
    }

    [Fact]
    public void RequestMoreInfo_ShouldTransitionBackToSubmitted()
    {
        var reviewerId = Guid.NewGuid();
        var application = CreateSubmittedApplication();

        application.RequestMoreInfo(reviewerId, "Please provide more details", _clock);

        application.Status.Should().Be(ApplicationStatus.Submitted);
        application.ReviewNotes.Should().Be("Please provide more details");
    }

    [Fact]
    public void StateTransitions_ShouldBeRecordedInHistory()
    {
        var reviewerId = Guid.NewGuid();
        var application = CreateSubmittedApplication();

        application.Approve(reviewerId, "Approved", _clock);

        application.StateHistory.Should().HaveCount(1);
        application.StateHistory.First().Status.Should().Be(ApplicationStatus.Approved);
    }

    [Fact]
    public void NewApplication_ShouldDefaultToVolunteerTier()
    {
        var application = new Application
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Motivation = "Test",
            SubmittedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };

        application.MembershipTier.Should().Be(MembershipTier.Volunteer);
    }

    [Theory]
    [InlineData(MembershipTier.Colaborador)]
    [InlineData(MembershipTier.Asociado)]
    public void Application_CanSetMembershipTier(MembershipTier tier)
    {
        var application = CreateSubmittedApplication();
        application.MembershipTier = tier;

        application.MembershipTier.Should().Be(tier);
    }

    [Fact]
    public void Application_CanSetTermExpiresAt()
    {
        var application = CreateSubmittedApplication();
        var expiryDate = new LocalDate(2027, 12, 31);

        application.TermExpiresAt = expiryDate;

        application.TermExpiresAt.Should().Be(expiryDate);
    }

    [Fact]
    public void Application_CanSetBoardMeetingDateAndDecisionNote()
    {
        var application = CreateSubmittedApplication();
        var meetingDate = new LocalDate(2026, 3, 15);

        application.BoardMeetingDate = meetingDate;
        application.DecisionNote = "Approved unanimously";

        application.BoardMeetingDate.Should().Be(meetingDate);
        application.DecisionNote.Should().Be("Approved unanimously");
    }

    [Fact]
    public void Application_CanSetRenewalReminderSentAt()
    {
        var application = CreateSubmittedApplication();
        var sentAt = _clock.GetCurrentInstant();

        application.RenewalReminderSentAt = sentAt;

        application.RenewalReminderSentAt.Should().Be(sentAt);
    }

    [Fact]
    public void Application_BoardVotes_ShouldBeEmptyByDefault()
    {
        var application = CreateSubmittedApplication();

        application.BoardVotes.Should().BeEmpty();
    }

    private Application CreateSubmittedApplication()
    {
        return new Application
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Motivation = "I want to join",
            SubmittedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
    }
}
