using AwesomeAssertions;
using NodaTime;
using NodaTime.Testing;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Xunit;

namespace Humans.Domain.Tests.Entities;

public class ProfileTests
{
    [HumansFact]
    public void FullName_ShouldCombineFirstAndLastName()
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            FirstName = "John",
            LastName = "Doe",
            CreatedAt = SystemClock.Instance.GetCurrentInstant(),
            UpdatedAt = SystemClock.Instance.GetCurrentInstant()
        };

        profile.FullName.Should().Be("John Doe");
    }

    [HumansFact]
    public void FullName_WithOnlyFirstName_ShouldReturnFirstName()
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            FirstName = "John",
            LastName = "",
            CreatedAt = SystemClock.Instance.GetCurrentInstant(),
            UpdatedAt = SystemClock.Instance.GetCurrentInstant()
        };

        profile.FullName.Should().Be("John");
    }

    [HumansFact]
    public void NewProfile_ShouldDefaultToVolunteerTier()
    {
        var profile = CreateProfile();

        profile.MembershipTier.Should().Be(MembershipTier.Volunteer);
    }

    [HumansFact]
    public void NewProfile_ShouldHaveNullConsentCheckStatus()
    {
        var profile = CreateProfile();

        profile.ConsentCheckStatus.Should().BeNull();
    }

    [HumansTheory]
    [InlineData(ConsentCheckStatus.Pending)]
    [InlineData(ConsentCheckStatus.Cleared)]
    [InlineData(ConsentCheckStatus.Flagged)]
    public void Profile_CanSetConsentCheckStatus(ConsentCheckStatus status)
    {
        var profile = CreateProfile();

        profile.ConsentCheckStatus = status;

        profile.ConsentCheckStatus.Should().Be(status);
    }

    [HumansFact]
    public void Profile_ConsentCheckCleared_SetsRelatedFields()
    {
        var profile = CreateProfile();
        var clock = new FakeClock(Instant.FromUtc(2026, 2, 15, 10, 0));
        var coordinatorId = Guid.NewGuid();

        profile.ConsentCheckStatus = ConsentCheckStatus.Cleared;
        profile.ConsentCheckAt = clock.GetCurrentInstant();
        profile.ConsentCheckedByUserId = coordinatorId;
        profile.ConsentCheckNotes = "Looks good";

        profile.ConsentCheckStatus.Should().Be(ConsentCheckStatus.Cleared);
        profile.ConsentCheckAt.Should().NotBeNull();
        profile.ConsentCheckedByUserId.Should().Be(coordinatorId);
        profile.ConsentCheckNotes.Should().Be("Looks good");
    }

    [HumansFact]
    public void Profile_Rejection_SetsRelatedFields()
    {
        var profile = CreateProfile();
        var clock = new FakeClock(Instant.FromUtc(2026, 2, 15, 10, 0));
        var adminId = Guid.NewGuid();

        profile.RejectionReason = "Safety concern";
        profile.RejectedAt = clock.GetCurrentInstant();
        profile.RejectedByUserId = adminId;

        profile.RejectionReason.Should().Be("Safety concern");
        profile.RejectedAt.Should().NotBeNull();
        profile.RejectedByUserId.Should().Be(adminId);
    }

    [HumansTheory]
    [InlineData(MembershipTier.Volunteer)]
    [InlineData(MembershipTier.Colaborador)]
    [InlineData(MembershipTier.Asociado)]
    public void Profile_CanSetMembershipTier(MembershipTier tier)
    {
        var profile = CreateProfile();

        profile.MembershipTier = tier;

        profile.MembershipTier.Should().Be(tier);
    }

    private static Profile CreateProfile()
    {
        return new Profile
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            FirstName = "Test",
            LastName = "User",
            IsApproved = true,
            CreatedAt = SystemClock.Instance.GetCurrentInstant(),
            UpdatedAt = SystemClock.Instance.GetCurrentInstant()
        };
    }

}
