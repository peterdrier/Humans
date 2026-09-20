using Humans.Users.Contracts;
using Humans.Governance.Contracts;
using AwesomeAssertions;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace Humans.Governance.Tests.Domain;

// Humans.Domain now references Humans.Interfaces, which declares the
// Humans.Application.* namespace, so the bare name `Application` would resolve
// to that namespace instead of the entity. The alias must sit inside the
// namespace declaration to win the lookup.
using Application = Humans.Governance.Domain.Application;

public class ApplicationTests
{
    private readonly FakeClock _clock = new(Instant.FromUtc(2024, 1, 15, 10, 0));

    [HumansFact]
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

    [HumansFact]
    public void Approve_ShouldTransitionToApproved()
    {
        var reviewerId = Guid.NewGuid();
        var application = CreateSubmittedApplication();

        application.Approve(reviewerId, "Welcome!", _clock);

        application.Status.Should().Be(ApplicationStatus.Approved);
        application.ReviewNotes.Should().Be("Welcome!");
        application.ResolvedAt.Should().NotBeNull();
    }

    [HumansFact]
    public void Reject_ShouldTransitionToRejected()
    {
        var reviewerId = Guid.NewGuid();
        var application = CreateSubmittedApplication();

        application.Reject(reviewerId, "Does not meet criteria", _clock);

        application.Status.Should().Be(ApplicationStatus.Rejected);
        application.ReviewNotes.Should().Be("Does not meet criteria");
        application.ResolvedAt.Should().NotBeNull();
    }

    [HumansFact]
    public void Withdraw_FromSubmitted_ShouldTransitionToWithdrawn()
    {
        var application = CreateSubmittedApplication();

        application.Withdraw(_clock);

        application.Status.Should().Be(ApplicationStatus.Withdrawn);
        application.ResolvedAt.Should().NotBeNull();
    }

    [HumansFact]
    public void RequestMoreInfo_ShouldTransitionBackToSubmitted()
    {
        var reviewerId = Guid.NewGuid();
        var application = CreateSubmittedApplication();

        application.RequestMoreInfo(reviewerId, "Please provide more details", _clock);

        application.Status.Should().Be(ApplicationStatus.Submitted);
        application.ReviewNotes.Should().Be("Please provide more details");
    }

    [HumansFact]
    public void StateTransitions_ShouldBeRecordedInHistory()
    {
        var reviewerId = Guid.NewGuid();
        var application = CreateSubmittedApplication();

        application.Approve(reviewerId, "Approved", _clock);

        application.StateHistory.Should().HaveCount(1);
        application.StateHistory.First().Status.Should().Be(ApplicationStatus.Approved);
    }

    /// <summary>
    /// Volunteer is the enum's zero member, so it is what an unset tier lands on — it is not a
    /// tier an application may carry, and a Volunteer never goes through Application at all.
    /// Pinned so a caller that forgets to set the tier is caught by <c>ValidateTier</c> rather
    /// than silently applying for whichever tier the enum's zero member happens to be.
    /// </summary>
    [HumansFact]
    public void ApplicationWithNoTierSet_LandsOnVolunteer_WhichValidateTierRejects()
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

        var act = application.ValidateTier;
        act.Should().Throw<InvalidOperationException>();
    }

    [HumansFact]
    public void ValidateTier_RejectsVolunteer()
    {
        var application = CreateSubmittedApplication();
        application.MembershipTier = MembershipTier.Volunteer;

        var act = application.ValidateTier;
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Volunteer*");
    }

    [HumansTheory]
    [InlineData(MembershipTier.Colaborador)]
    [InlineData(MembershipTier.Asociado)]
    public void ValidateTier_AcceptsValidTiers(MembershipTier tier)
    {
        var application = CreateSubmittedApplication();
        application.MembershipTier = tier;

        var act = application.ValidateTier;
        act.Should().NotThrow();
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
