using FluentAssertions;
using NodaTime;
using Profiles.Domain.Entities;
using Xunit;

namespace Profiles.Domain.Tests.Entities;

public class RoleAssignmentTests
{
    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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
}
