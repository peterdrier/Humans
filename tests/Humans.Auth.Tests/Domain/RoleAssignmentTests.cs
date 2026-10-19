using AwesomeAssertions;
using NodaTime;
using Xunit;
using Humans.Auth.Contracts;
using Humans.Auth.Domain;

namespace Humans.Auth.Tests.Domain;

public sealed class RoleAssignmentTests
{
    [HumansFact]
    public void IsActive_WithinValidPeriod_ShouldReturnTrue()
    {
        var now = Instant.FromUtc(2024, 6, 15, 12, 0);
        var assignment = new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            RoleName = "Member",
            ValidFrom = Instant.FromUtc(2024, 1, 1, 0, 0),
            ValidTo = Instant.FromUtc(2024, 12, 31, 23, 59),
            CreatedAt = Instant.FromUtc(2024, 1, 1, 0, 0),
            CreatedByUserId = Guid.NewGuid()
        };

        assignment.IsActive(now).Should().BeTrue();
    }

    [HumansFact]
    public void IsActive_BeforeValidFrom_ShouldReturnFalse()
    {
        var now = Instant.FromUtc(2023, 12, 15, 12, 0);
        var assignment = new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            RoleName = "Member",
            ValidFrom = Instant.FromUtc(2024, 1, 1, 0, 0),
            ValidTo = Instant.FromUtc(2024, 12, 31, 23, 59),
            CreatedAt = Instant.FromUtc(2023, 12, 1, 0, 0),
            CreatedByUserId = Guid.NewGuid()
        };

        assignment.IsActive(now).Should().BeFalse();
    }

    [HumansFact]
    public void IsActive_AfterValidTo_ShouldReturnFalse()
    {
        var now = Instant.FromUtc(2025, 1, 15, 12, 0);
        var assignment = new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            RoleName = "Member",
            ValidFrom = Instant.FromUtc(2024, 1, 1, 0, 0),
            ValidTo = Instant.FromUtc(2024, 12, 31, 23, 59),
            CreatedAt = Instant.FromUtc(2024, 1, 1, 0, 0),
            CreatedByUserId = Guid.NewGuid()
        };

        assignment.IsActive(now).Should().BeFalse();
    }

    [HumansFact]
    public void IsActive_WithNoExpiration_ShouldReturnTrueAfterValidFrom()
    {
        var now = Instant.FromUtc(2030, 1, 15, 12, 0);
        var assignment = new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            RoleName = "Member",
            ValidFrom = Instant.FromUtc(2024, 1, 1, 0, 0),
            ValidTo = null, // No expiration
            CreatedAt = Instant.FromUtc(2024, 1, 1, 0, 0),
            CreatedByUserId = Guid.NewGuid()
        };

        assignment.IsActive(now).Should().BeTrue();
    }

    [HumansFact]
    public void IsActive_ExactlyAtValidFrom_ShouldReturnTrue()
    {
        var validFrom = Instant.FromUtc(2024, 1, 1, 0, 0);
        var assignment = new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            RoleName = "Member",
            ValidFrom = validFrom,
            ValidTo = null,
            CreatedAt = validFrom,
            CreatedByUserId = Guid.NewGuid()
        };

        assignment.IsActive(validFrom).Should().BeTrue();
    }

    [HumansFact]
    public void IsActive_ExactlyAtValidTo_ShouldReturnFalse()
    {
        var validTo = Instant.FromUtc(2024, 12, 31, 23, 59);
        var assignment = new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            RoleName = "Member",
            ValidFrom = Instant.FromUtc(2024, 1, 1, 0, 0),
            ValidTo = validTo,
            CreatedAt = Instant.FromUtc(2024, 1, 1, 0, 0),
            CreatedByUserId = Guid.NewGuid()
        };

        assignment.IsActive(validTo).Should().BeFalse();
    }

    // The cached row and the entity must answer "active at now" identically.
    [HumansTheory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(10, true)]
    [InlineData(20, false)]
    [InlineData(21, false)]
    public void RoleAssignmentRow_IsActiveAt_AgreesWithIsActive_AtTheBoundaries(int daysFromValidFrom, bool expected)
    {
        var validFrom = Instant.FromUtc(2024, 1, 1, 0, 0);
        var validTo = validFrom + Duration.FromDays(20);
        var now = validFrom + Duration.FromDays(daysFromValidFrom);
        var assignment = new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            RoleName = "Member",
            ValidFrom = validFrom,
            ValidTo = validTo,
            CreatedAt = validFrom,
            CreatedByUserId = Guid.NewGuid()
        };
        var row = new RoleAssignmentRow(assignment.Id, assignment.UserId, assignment.RoleName, validFrom, validTo);

        row.IsActiveAt(now).Should().Be(expected);
        assignment.IsActive(now).Should().Be(expected);
    }
}
