using FluentAssertions;
using NodaTime;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Xunit;

namespace Humans.Domain.Tests.Entities;

public class ProfileTests
{
    [Fact]
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

    [Fact]
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

    [Fact]
    public void ComputeMembershipStatus_WhenSuspended_ShouldReturnSuspended()
    {
        var profile = CreateProfile();
        profile.IsSuspended = true;

        var status = profile.ComputeMembershipStatus(
            CreateActiveRoleAssignments(),
            [Guid.NewGuid()],
            [Guid.NewGuid()]);

        status.Should().Be(MembershipStatus.Suspended);
    }

    [Fact]
    public void ComputeMembershipStatus_WithNoActiveRoles_ShouldReturnNone()
    {
        var profile = CreateProfile();

        var status = profile.ComputeMembershipStatus(
            [], // No roles
            [Guid.NewGuid()],
            [Guid.NewGuid()]);

        status.Should().Be(MembershipStatus.None);
    }

    [Fact]
    public void ComputeMembershipStatus_WhenNotApproved_ShouldReturnPending()
    {
        var profile = CreateProfile();
        profile.IsApproved = false;

        var status = profile.ComputeMembershipStatus(
            CreateActiveRoleAssignments(),
            [Guid.NewGuid()],
            [Guid.NewGuid()]);

        status.Should().Be(MembershipStatus.Pending);
    }

    [Fact]
    public void ComputeMembershipStatus_WithMissingConsent_ShouldReturnInactive()
    {
        var profile = CreateProfile();
        var requiredDocId = Guid.NewGuid();

        var status = profile.ComputeMembershipStatus(
            CreateActiveRoleAssignments(),
            [requiredDocId],
            []); // No consents

        status.Should().Be(MembershipStatus.Inactive);
    }

    [Fact]
    public void ComputeMembershipStatus_WithAllConsents_ShouldReturnActive()
    {
        var profile = CreateProfile();
        var docId1 = Guid.NewGuid();
        var docId2 = Guid.NewGuid();

        var status = profile.ComputeMembershipStatus(
            CreateActiveRoleAssignments(),
            [docId1, docId2],
            [docId1, docId2]);

        status.Should().Be(MembershipStatus.Active);
    }

    [Fact]
    public void ComputeMembershipStatus_WithPartialConsent_ShouldReturnInactive()
    {
        var profile = CreateProfile();
        var docId1 = Guid.NewGuid();
        var docId2 = Guid.NewGuid();

        var status = profile.ComputeMembershipStatus(
            CreateActiveRoleAssignments(),
            [docId1, docId2],
            [docId1]); // Only one consent

        status.Should().Be(MembershipStatus.Inactive);
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

    private static List<RoleAssignment> CreateActiveRoleAssignments()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        return
        [
            new RoleAssignment
            {
                Id = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                RoleName = "Member",
                ValidFrom = now - Duration.FromDays(30),
                ValidTo = null,
                CreatedAt = now - Duration.FromDays(30),
                CreatedByUserId = Guid.NewGuid()
            }
        ];
    }
}
