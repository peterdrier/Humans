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
    public void StartReview_ShouldTransitionToUnderReview()
    {
        var reviewerId = Guid.NewGuid();
        var application = CreateSubmittedApplication();

        application.StartReview(reviewerId, _clock);

        application.Status.Should().Be(ApplicationStatus.UnderReview);
        application.ReviewedByUserId.Should().Be(reviewerId);
        application.ReviewStartedAt.Should().NotBeNull();
    }

    [Fact]
    public void Approve_ShouldTransitionToApproved()
    {
        var reviewerId = Guid.NewGuid();
        var application = CreateApplicationUnderReview(reviewerId);

        application.Approve(reviewerId, "Welcome!", _clock);

        application.Status.Should().Be(ApplicationStatus.Approved);
        application.ReviewNotes.Should().Be("Welcome!");
        application.ResolvedAt.Should().NotBeNull();
    }

    [Fact]
    public void Reject_ShouldTransitionToRejected()
    {
        var reviewerId = Guid.NewGuid();
        var application = CreateApplicationUnderReview(reviewerId);

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
    public void Withdraw_FromUnderReview_ShouldTransitionToWithdrawn()
    {
        var reviewerId = Guid.NewGuid();
        var application = CreateApplicationUnderReview(reviewerId);

        application.Withdraw(_clock);

        application.Status.Should().Be(ApplicationStatus.Withdrawn);
    }

    [Fact]
    public void RequestMoreInfo_ShouldTransitionBackToSubmitted()
    {
        var reviewerId = Guid.NewGuid();
        var application = CreateApplicationUnderReview(reviewerId);

        application.RequestMoreInfo(reviewerId, "Please provide more details", _clock);

        application.Status.Should().Be(ApplicationStatus.Submitted);
        application.ReviewNotes.Should().Be("Please provide more details");
    }

    [Fact]
    public void StateTransitions_ShouldBeRecordedInHistory()
    {
        var reviewerId = Guid.NewGuid();
        var application = CreateSubmittedApplication();

        application.StartReview(reviewerId, _clock);
        application.Approve(reviewerId, "Approved", _clock);

        application.StateHistory.Should().HaveCount(2);
        application.StateHistory.First().Status.Should().Be(ApplicationStatus.UnderReview);
        application.StateHistory.Last().Status.Should().Be(ApplicationStatus.Approved);
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

    private Application CreateApplicationUnderReview(Guid reviewerId)
    {
        var application = CreateSubmittedApplication();
        application.StartReview(reviewerId, _clock);
        return application;
    }
}
