using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Humans.Domain.Entities;
using Xunit;

namespace Humans.Application.Tests.Services;

public class RoleAssignmentServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly RoleAssignmentService _service;

    public RoleAssignmentServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 2, 15, 15, 30));
        _service = new RoleAssignmentService(_dbContext);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task HasOverlappingAssignmentAsync_NoAssignments_ReturnsFalse()
    {
        var userId = Guid.NewGuid();

        var result = await _service.HasOverlappingAssignmentAsync(userId, "Board", _clock.GetCurrentInstant());

        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasOverlappingAssignmentAsync_PastEndedWindow_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        await AddAssignmentAsync(
            userId,
            "Board",
            _clock.GetCurrentInstant() - Duration.FromDays(20),
            _clock.GetCurrentInstant() - Duration.FromDays(10));

        var result = await _service.HasOverlappingAssignmentAsync(userId, "Board", _clock.GetCurrentInstant());

        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasOverlappingAssignmentAsync_OpenEndedActiveWindow_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        await AddAssignmentAsync(
            userId,
            "Board",
            _clock.GetCurrentInstant() - Duration.FromDays(5),
            null);

        var result = await _service.HasOverlappingAssignmentAsync(userId, "Board", _clock.GetCurrentInstant());

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasOverlappingAssignmentAsync_FutureWindow_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        await AddAssignmentAsync(
            userId,
            "Board",
            _clock.GetCurrentInstant() + Duration.FromDays(10),
            null);

        var result = await _service.HasOverlappingAssignmentAsync(userId, "Board", _clock.GetCurrentInstant());

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasOverlappingAssignmentAsync_DifferentRole_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        await AddAssignmentAsync(
            userId,
            "Lead",
            _clock.GetCurrentInstant() - Duration.FromDays(5),
            null);

        var result = await _service.HasOverlappingAssignmentAsync(userId, "Board", _clock.GetCurrentInstant());

        result.Should().BeFalse();
    }

    private async Task AddAssignmentAsync(Guid userId, string roleName, Instant validFrom, Instant? validTo)
    {
        _dbContext.RoleAssignments.Add(new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleName = roleName,
            ValidFrom = validFrom,
            ValidTo = validTo,
            CreatedAt = validFrom,
            CreatedByUserId = Guid.NewGuid()
        });

        await _dbContext.SaveChangesAsync();
    }
}
