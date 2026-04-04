using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using Humans.Application;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Humans.Domain.Entities;
using Humans.Domain.Constants;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

public class RoleAssignmentServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly RoleAssignmentService _service;
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    public RoleAssignmentServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 2, 15, 15, 30));
        // These tests only exercise HasOverlappingAssignmentAsync which uses only _dbContext
        _service = new RoleAssignmentService(
            _dbContext,
            Substitute.For<Humans.Application.Interfaces.IAuditLogService>(),
            Substitute.For<Humans.Application.Interfaces.INotificationService>(),
            Substitute.For<Humans.Application.Interfaces.ISystemTeamSync>(),
            _clock,
            _cache,
            NullLogger<RoleAssignmentService>.Instance);
    }

    public void Dispose()
    {
        _cache.Dispose();
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

    [Fact]
    public async Task AssignRoleAsync_InvalidatesCachedClaimsForUser()
    {
        var userId = Guid.NewGuid();
        var assignerId = Guid.NewGuid();
        await SeedUserAsync(userId, "Target User");
        await SeedUserAsync(assignerId, "Admin User");
        _cache.Set(CacheKeys.RoleAssignmentClaims(userId), new[] { "stale-claim" });

        var result = await _service.AssignRoleAsync(userId, RoleNames.Board, assignerId, null);

        result.Success.Should().BeTrue();
        _cache.TryGetValue(CacheKeys.RoleAssignmentClaims(userId), out _).Should().BeFalse();
    }

    [Fact]
    public async Task EndRoleAsync_InvalidatesCachedClaimsForUser()
    {
        var userId = Guid.NewGuid();
        var enderId = Guid.NewGuid();
        await SeedUserAsync(userId, "Target User");
        await SeedUserAsync(enderId, "Admin User");
        var assignment = await AddAssignmentAsync(
            userId,
            RoleNames.Board,
            _clock.GetCurrentInstant() - Duration.FromDays(1),
            null);
        _cache.Set(CacheKeys.RoleAssignmentClaims(userId), new[] { "stale-claim" });

        var result = await _service.EndRoleAsync(assignment.Id, enderId, null);

        result.Success.Should().BeTrue();
        _cache.TryGetValue(CacheKeys.RoleAssignmentClaims(userId), out _).Should().BeFalse();
    }

    private async Task<User> SeedUserAsync(Guid userId, string displayName)
    {
        var user = new User
        {
            Id = userId,
            DisplayName = displayName,
            UserName = $"test-{userId}@test.com",
            Email = $"test-{userId}@test.com",
            PreferredLanguage = "en"
        };

        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();
        return user;
    }

    private async Task<RoleAssignment> AddAssignmentAsync(Guid userId, string roleName, Instant validFrom, Instant? validTo)
    {
        var assignment = new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleName = roleName,
            ValidFrom = validFrom,
            ValidTo = validTo,
            CreatedAt = validFrom,
            CreatedByUserId = Guid.NewGuid()
        };

        _dbContext.RoleAssignments.Add(assignment);

        await _dbContext.SaveChangesAsync();
        return assignment;
    }
}
