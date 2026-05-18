using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Humans.Application.Tests.Infrastructure;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Constants;
using NSubstitute;
using RoleAssignmentService = Humans.Application.Services.Auth.RoleAssignmentService;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Infrastructure.Repositories.Auth;

namespace Humans.Application.Tests.Services;

public sealed class RoleAssignmentServiceTests : ServiceTestHarness
{
    private readonly IRoleAssignmentRepository _repository;
    private readonly IUserService _userService;
    private readonly INavBadgeCacheInvalidator _navBadge;
    private readonly IRoleAssignmentClaimsCacheInvalidator _claimsInvalidator;
    private readonly RoleAssignmentService _service;

    public RoleAssignmentServiceTests()
        : base(Instant.FromUtc(2026, 2, 15, 15, 30))
    {
        _repository = new RoleAssignmentRepository(DbFactory);

        _userService = NewDbBackedUserService();

        _navBadge = Substitute.For<INavBadgeCacheInvalidator>();
        _claimsInvalidator = Substitute.For<IRoleAssignmentClaimsCacheInvalidator>();

        _service = new RoleAssignmentService(
            _repository,
            _userService,
            Substitute.For<IAuditLogService>(),
            Substitute.For<INotificationEmitter>(),
            Substitute.For<ISystemTeamSync>(),
            _navBadge,
            _claimsInvalidator,
            Substitute.For<IRoleAssignmentCacheInvalidator>(),
            Clock,
            NullLogger<RoleAssignmentService>.Instance);
    }

    [HumansFact]
    public async Task HasOverlappingAssignmentAsync_NoAssignments_ReturnsFalse()
    {
        var userId = Guid.NewGuid();

        var result = await _service.HasOverlappingAssignmentAsync(userId, "Board", Clock.GetCurrentInstant());

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task HasOverlappingAssignmentAsync_PastEndedWindow_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        await AddAssignmentAsync(
            userId,
            "Board",
            Clock.GetCurrentInstant() - Duration.FromDays(20),
            Clock.GetCurrentInstant() - Duration.FromDays(10));

        var result = await _service.HasOverlappingAssignmentAsync(userId, "Board", Clock.GetCurrentInstant());

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task HasOverlappingAssignmentAsync_OpenEndedActiveWindow_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        await AddAssignmentAsync(
            userId,
            "Board",
            Clock.GetCurrentInstant() - Duration.FromDays(5),
            null);

        var result = await _service.HasOverlappingAssignmentAsync(userId, "Board", Clock.GetCurrentInstant());

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task HasOverlappingAssignmentAsync_FutureWindow_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        await AddAssignmentAsync(
            userId,
            "Board",
            Clock.GetCurrentInstant() + Duration.FromDays(10),
            null);

        var result = await _service.HasOverlappingAssignmentAsync(userId, "Board", Clock.GetCurrentInstant());

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task HasOverlappingAssignmentAsync_DifferentRole_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        await AddAssignmentAsync(
            userId,
            "Lead",
            Clock.GetCurrentInstant() - Duration.FromDays(5),
            null);

        var result = await _service.HasOverlappingAssignmentAsync(userId, "Board", Clock.GetCurrentInstant());

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task AssignRoleAsync_InvalidatesCachedClaimsForUser()
    {
        var userId = Guid.NewGuid();
        var assignerId = Guid.NewGuid();
        await SeedUserAsync(userId, "Target User");
        await SeedUserAsync(assignerId, "Admin User");

        var result = await _service.AssignRoleAsync(
            userId, RoleNames.Board, assignerId, null);

        result.Success.Should().BeTrue();
        _claimsInvalidator.Received(1).Invalidate(userId);
        _navBadge.Received(1).Invalidate();
    }

    [HumansFact]
    public async Task EndRoleAsync_InvalidatesCachedClaimsForUser()
    {
        var userId = Guid.NewGuid();
        var enderId = Guid.NewGuid();
        await SeedUserAsync(userId, "Target User");
        await SeedUserAsync(enderId, "Admin User");
        var assignment = await AddAssignmentAsync(
            userId,
            RoleNames.Board,
            Clock.GetCurrentInstant() - Duration.FromDays(1),
            null);

        var result = await _service.EndRoleAsync(
            assignment.Id, enderId, null);

        result.Success.Should().BeTrue();
        _claimsInvalidator.Received(1).Invalidate(userId);
        _navBadge.Received(1).Invalidate();
    }

    [HumansFact]
    public async Task GetByUserIdAsync_ReturnsSummarySnapshots()
    {
        var userId = Guid.NewGuid();
        var creatorId = Guid.NewGuid();
        await SeedUserAsync(userId, "Target User");
        await SeedUserAsync(creatorId, "Creator User");
        var assignment = await AddAssignmentAsync(
            userId,
            RoleNames.Board,
            Clock.GetCurrentInstant() - Duration.FromDays(1),
            null,
            creatorId);

        var result = await _service.GetByUserIdAsync(userId);

        result.Should().ContainSingle();
        result[0].Id.Should().Be(assignment.Id);
        result[0].UserId.Should().Be(userId);
        result[0].RoleName.Should().Be(RoleNames.Board);
        result[0].CreatedByUserId.Should().Be(creatorId);
    }

    [HumansFact]
    public async Task GetFilteredAsync_ReturnsSummarySnapshotsForAllReturnedAssignments()
    {
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();
        var creator = Guid.NewGuid();
        await SeedUserAsync(user1, "Alice");
        await SeedUserAsync(user2, "Bob");
        await SeedUserAsync(creator, "Creator");
        await AddAssignmentAsync(user1, RoleNames.Board, Clock.GetCurrentInstant() - Duration.FromDays(1), null, creator);
        await AddAssignmentAsync(user2, RoleNames.Board, Clock.GetCurrentInstant() - Duration.FromDays(2), null, creator);

        var (items, total) = await _service.GetFilteredAsync(
            roleFilter: RoleNames.Board, activeOnly: true, page: 1, pageSize: 50, Clock.GetCurrentInstant());

        items.Should().HaveCount(2);
        total.Should().Be(2);
        items.Select(ra => ra.UserDisplayName).Should().BeEquivalentTo("Alice", "Bob");
        items.All(ra => string.Equals(ra.CreatedByDisplayName, "Creator", StringComparison.Ordinal)).Should().BeTrue();
    }

    [HumansFact]
    public async Task RevokeAllActiveAsync_EndsAllActive_AndInvalidatesClaims()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId, "Target");
        await AddAssignmentAsync(userId, RoleNames.Board, Clock.GetCurrentInstant() - Duration.FromDays(10), null);
        await AddAssignmentAsync(userId, RoleNames.Admin, Clock.GetCurrentInstant() - Duration.FromDays(5), null);

        var count = await _service.RevokeAllActiveAsync(userId);

        count.Should().Be(2);
        var remaining = await Db.RoleAssignments
            .AsNoTracking()
            .Where(ra => ra.UserId == userId)
            .ToListAsync();
        remaining.All(ra => ra.ValidTo.HasValue).Should().BeTrue();
        _claimsInvalidator.Received(1).Invalidate(userId);
    }

    [HumansFact]
    public async Task AssignRoleAsync_RoleAlreadyActive_ReturnsFailure()
    {
        var userId = Guid.NewGuid();
        var assignerId = Guid.NewGuid();
        await SeedUserAsync(userId, "Target");
        await SeedUserAsync(assignerId, "Admin");
        await AddAssignmentAsync(userId, RoleNames.Board, Clock.GetCurrentInstant() - Duration.FromDays(1), null);

        var result = await _service.AssignRoleAsync(userId, RoleNames.Board, assignerId, null);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("RoleAlreadyActive");
    }

    [HumansFact]
    public async Task EndRoleAsync_NotFound_ReturnsFailure()
    {
        var result = await _service.EndRoleAsync(Guid.NewGuid(), Guid.NewGuid(), null);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotFound");
    }

    [HumansFact]
    public async Task ContributeForUserAsync_ReturnsRoleAssignmentsSlice()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId, "Target");
        await AddAssignmentAsync(userId, RoleNames.Board, Clock.GetCurrentInstant() - Duration.FromDays(1), null);

        var slices = await _service.ContributeForUserAsync(userId, CancellationToken.None);

        slices.Should().ContainSingle();
        slices[0].SectionName.Should().Be(
            Interfaces.Gdpr.GdprExportSections.RoleAssignments);
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

        Db.Users.Add(user);
        await Db.SaveChangesAsync();
        return user;
    }

    private async Task<RoleAssignment> AddAssignmentAsync(
        Guid userId, string roleName, Instant validFrom, Instant? validTo, Guid? createdByUserId = null)
    {
        var assignment = new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleName = roleName,
            ValidFrom = validFrom,
            ValidTo = validTo,
            CreatedAt = validFrom,
            CreatedByUserId = createdByUserId ?? Guid.NewGuid()
        };

        Db.RoleAssignments.Add(assignment);

        await Db.SaveChangesAsync();
        return assignment;
    }
}
