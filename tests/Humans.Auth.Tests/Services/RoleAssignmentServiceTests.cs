using AwesomeAssertions;
using Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Humans.Auth.Tests.Infrastructure;
using Humans.Base.Interfaces.Caching;
using Humans.Auth.Data;
using Humans.Auth.Domain;
using Humans.Auth.Services;
using Humans.AuditLog.Contracts;
using Humans.Base.Constants;
using Humans.Notifications.Contracts;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Humans.Users.Contracts;


using Humans.Teams.Contracts;

namespace Humans.Auth.Tests.Services;

public sealed class RoleAssignmentServiceTests : AuthTestHarness
{
    private readonly IRoleAssignmentRepository _repository;
    private readonly IUserServiceRead _userService;
    private readonly INavBadgeCacheInvalidator _navBadge;
    private readonly IRoleAssignmentClaimsCacheInvalidator _claimsInvalidator;
    private readonly IRoleAssignmentCacheInvalidator _rowCache;
    private readonly ISystemTeamSync _systemTeamSync;
    private readonly RoleAssignmentService _service;

    public RoleAssignmentServiceTests()
        : base(Instant.FromUtc(2026, 2, 15, 15, 30))
    {
        _repository = new RoleAssignmentRepository(AuthDbFactory);

        _userService = NewStubUserService();

        _navBadge = Substitute.For<INavBadgeCacheInvalidator>();
        _claimsInvalidator = Substitute.For<IRoleAssignmentClaimsCacheInvalidator>();
        _rowCache = Substitute.For<IRoleAssignmentCacheInvalidator>();
        _systemTeamSync = Substitute.For<ISystemTeamSync>();

        _service = new RoleAssignmentService(
            _repository,
            _userService,
            AuditLog,
            Notifier,
            _systemTeamSync,
            _navBadge,
            _claimsInvalidator,
            _rowCache,
            Clock,
            NullLogger<RoleAssignmentService>.Instance);
    }

    [HumansFact]
    public async Task HasOverlappingAssignmentAsync_NoAssignments_ReturnsFalse()
    {
        var userId = Guid.NewGuid();

        var result = await _service.HasOverlappingAssignmentAsync(userId, "Board", Clock.GetCurrentInstant(), cancellationToken: TestContext.Current.CancellationToken);

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

        var result = await _service.HasOverlappingAssignmentAsync(userId, "Board", Clock.GetCurrentInstant(), cancellationToken: TestContext.Current.CancellationToken);

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

        var result = await _service.HasOverlappingAssignmentAsync(userId, "Board", Clock.GetCurrentInstant(), cancellationToken: TestContext.Current.CancellationToken);

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

        var result = await _service.HasOverlappingAssignmentAsync(userId, "Board", Clock.GetCurrentInstant(), cancellationToken: TestContext.Current.CancellationToken);

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

        var result = await _service.HasOverlappingAssignmentAsync(userId, "Board", Clock.GetCurrentInstant(), cancellationToken: TestContext.Current.CancellationToken);

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
            userId, RoleNames.Board, assignerId, null, TestContext.Current.CancellationToken);

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
            assignment.Id, enderId, null, TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        _claimsInvalidator.Received(1).Invalidate(userId);
        _navBadge.Received(1).Invalidate();
    }

    [HumansFact]
    public async Task AssignRoleAsync_InvalidatesRowCache_AndWritesAudit()
    {
        // The row cache is a Singleton holding every role_assignments row; a write
        // that skips InvalidateAll leaves every reader on stale roles until restart.
        var userId = Guid.NewGuid();
        var assignerId = Guid.NewGuid();
        await SeedUserAsync(userId, "Target User");
        await SeedUserAsync(assignerId, "Admin User");

        await _service.AssignRoleAsync(
            userId, RoleNames.Board, assignerId, null, TestContext.Current.CancellationToken);

        _rowCache.Received(1).InvalidateAll();
        await AuditLog.Received(1).LogAsync(
            AuditAction.RoleAssigned, nameof(User), userId,
            Arg.Is<string>(d => d.Contains(RoleNames.Board, StringComparison.Ordinal)),
            assignerId, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task EndRoleAsync_InvalidatesRowCache_AndWritesAudit()
    {
        var userId = Guid.NewGuid();
        var enderId = Guid.NewGuid();
        await SeedUserAsync(userId, "Target User");
        await SeedUserAsync(enderId, "Admin User");
        var assignment = await AddAssignmentAsync(
            userId, RoleNames.Board, Clock.GetCurrentInstant() - Duration.FromDays(1), null);

        await _service.EndRoleAsync(
            assignment.Id, enderId, null, TestContext.Current.CancellationToken);

        _rowCache.Received(1).InvalidateAll();
        await AuditLog.Received(1).LogAsync(
            AuditAction.RoleEnded, nameof(User), userId,
            Arg.Is<string>(d => d.Contains(RoleNames.Board, StringComparison.Ordinal)),
            enderId, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task AssignRoleAsync_Board_SyncsBoardSystemTeam_OtherRolesDoNot()
    {
        // The Board system team's membership mirrors the Board role. The sync is
        // guarded on the role name, so both arms of that guard are behaviour.
        var boardUser = Guid.NewGuid();
        var adminUser = Guid.NewGuid();
        var assignerId = Guid.NewGuid();
        await SeedUserAsync(boardUser, "Board Member");
        await SeedUserAsync(adminUser, "Admin Member");
        await SeedUserAsync(assignerId, "Assigner");

        await _service.AssignRoleAsync(
            adminUser, RoleNames.Admin, assignerId, null, TestContext.Current.CancellationToken);
        await _systemTeamSync.DidNotReceive().SyncBoardTeamAsync();

        await _service.AssignRoleAsync(
            boardUser, RoleNames.Board, assignerId, null, TestContext.Current.CancellationToken);
        await _systemTeamSync.Received(1).SyncBoardTeamAsync();
    }

    [HumansFact]
    public async Task EndRoleAsync_Board_SyncsBoardSystemTeam_OtherRolesDoNot()
    {
        var boardUser = Guid.NewGuid();
        var adminUser = Guid.NewGuid();
        var enderId = Guid.NewGuid();
        await SeedUserAsync(boardUser, "Board Member");
        await SeedUserAsync(adminUser, "Admin Member");
        await SeedUserAsync(enderId, "Ender");
        var yesterday = Clock.GetCurrentInstant() - Duration.FromDays(1);
        var adminRow = await AddAssignmentAsync(adminUser, RoleNames.Admin, yesterday, null);
        var boardRow = await AddAssignmentAsync(boardUser, RoleNames.Board, yesterday, null);

        await _service.EndRoleAsync(adminRow.Id, enderId, null, TestContext.Current.CancellationToken);
        await _systemTeamSync.DidNotReceive().SyncBoardTeamAsync();

        await _service.EndRoleAsync(boardRow.Id, enderId, null, TestContext.Current.CancellationToken);
        await _systemTeamSync.Received(1).SyncBoardTeamAsync();
    }

    [HumansFact]
    public async Task EndRoleAsync_AlreadyEnded_ReturnsRoleNotActive()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId, "Target");
        var assignment = await AddAssignmentAsync(
            userId,
            RoleNames.Board,
            Clock.GetCurrentInstant() - Duration.FromDays(10),
            Clock.GetCurrentInstant() - Duration.FromDays(1));

        var result = await _service.EndRoleAsync(
            assignment.Id, Guid.NewGuid(), null, TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("RoleNotActive");
    }

    [HumansFact]
    public async Task EndRoleAsync_NotYetActive_ReturnsRoleNotActive()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId, "Target");
        var assignment = await AddAssignmentAsync(
            userId, RoleNames.Board, Clock.GetCurrentInstant() + Duration.FromDays(1), null);

        var result = await _service.EndRoleAsync(
            assignment.Id, Guid.NewGuid(), null, TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("RoleNotActive");
    }

    [HumansFact]
    public async Task AssignRoleAsync_NotificationThrows_StillSucceeds()
    {
        // The in-app notification is best-effort. A Notifications outage must not
        // roll a role assignment back or surface as a failure to the admin.
        var userId = Guid.NewGuid();
        var assignerId = Guid.NewGuid();
        await SeedUserAsync(userId, "Target");
        await SeedUserAsync(assignerId, "Admin");
        Notifier
            .SendAsync(
                Arg.Any<NotificationSource>(), Arg.Any<NotificationClass>(),
                Arg.Any<NotificationPriority>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<Guid>>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("notifications down"));

        var result = await _service.AssignRoleAsync(
            userId, RoleNames.Board, assignerId, null, TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        var rows = await AuthDb.RoleAssignments.AsNoTracking()
            .Where(ra => ra.UserId == userId)
            .ToListAsync(TestContext.Current.CancellationToken);
        rows.Should().ContainSingle();
    }

    [HumansFact]
    public async Task RevokeAllActiveAsync_StaysSilent_UnlikeTheAdminWritePaths()
    {
        // Deliberate asymmetry: bulk revoke is the account-deletion/privacy path, not
        // an admin role-management action, so it invalidates caches but dispatches no
        // per-row notification and does not bump the nav-badge counters. The same
        // roles ended one at a time through EndRoleAsync do both.
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId, "Target");
        await AddAssignmentAsync(userId, RoleNames.Board, Clock.GetCurrentInstant() - Duration.FromDays(10), null);
        await AddAssignmentAsync(userId, RoleNames.Admin, Clock.GetCurrentInstant() - Duration.FromDays(5), null);

        await _service.RevokeAllActiveAsync(userId, TestContext.Current.CancellationToken);

        _rowCache.Received(1).InvalidateAll();
        _navBadge.DidNotReceive().Invalidate();
        await Notifier.DidNotReceive().SendAsync(
            Arg.Any<NotificationSource>(), Arg.Any<NotificationClass>(),
            Arg.Any<NotificationPriority>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<Guid>>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
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

        var result = await _service.GetByUserIdAsync(userId, TestContext.Current.CancellationToken);

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
            roleFilter: RoleNames.Board, activeOnly: true, page: 1, pageSize: 50, Clock.GetCurrentInstant(), ct: TestContext.Current.CancellationToken);

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

        var count = await _service.RevokeAllActiveAsync(userId, TestContext.Current.CancellationToken);

        count.Should().Be(2);
        var remaining = await AuthDb.RoleAssignments
            .AsNoTracking()
            .Where(ra => ra.UserId == userId)
            .ToListAsync(TestContext.Current.CancellationToken);
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

        var result = await _service.AssignRoleAsync(userId, RoleNames.Board, assignerId, null, TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("RoleAlreadyActive");
    }

    [HumansFact]
    public async Task EndRoleAsync_NotFound_ReturnsFailure()
    {
        var result = await _service.EndRoleAsync(Guid.NewGuid(), Guid.NewGuid(), null, TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotFound");
    }

    [HumansTheory]
    [InlineData(null, 0, null, false)]
    [InlineData(RoleNames.Admin, -10, null, true)]
    [InlineData(RoleNames.Admin, -30, -1, false)]
    [InlineData(RoleNames.Admin, 1, null, false)]
    [InlineData(RoleNames.Board, -10, null, false)]
    public async Task IsUserAdminAsync_HonorsActiveAdminWindow(
        string? seededRole, int validFromDays, int? validToDays, bool expected)
    {
        var user = SeedUser();
        if (seededRole is not null)
        {
            SeedRoleAssignment(
                user.Id,
                seededRole,
                Clock.GetCurrentInstant() + Duration.FromDays(validFromDays),
                validToDays.HasValue ? Clock.GetCurrentInstant() + Duration.FromDays(validToDays.Value) : null);
        }

        var result = await _service.IsUserAdminAsync(user.Id, TestContext.Current.CancellationToken);

        result.Should().Be(expected);
    }

    [HumansTheory]
    [InlineData(null, 0, null, false)]
    [InlineData(RoleNames.Board, -10, null, true)]
    [InlineData(RoleNames.Board, -30, -1, false)]
    [InlineData(RoleNames.Admin, -10, null, false)]
    public async Task IsUserBoardMemberAsync_HonorsActiveBoardWindow(
        string? seededRole, int validFromDays, int? validToDays, bool expected)
    {
        var user = SeedUser();
        if (seededRole is not null)
        {
            SeedRoleAssignment(
                user.Id,
                seededRole,
                Clock.GetCurrentInstant() + Duration.FromDays(validFromDays),
                validToDays.HasValue ? Clock.GetCurrentInstant() + Duration.FromDays(validToDays.Value) : null);
        }

        var result = await _service.IsUserBoardMemberAsync(user.Id, TestContext.Current.CancellationToken);

        result.Should().Be(expected);
    }

    [HumansFact]
    public async Task ContributeForUserAsync_ReturnsRoleAssignmentsSlice()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId, "Target");
        await AddAssignmentAsync(userId, RoleNames.Board, Clock.GetCurrentInstant() - Duration.FromDays(1), null);

        var slices = await _service.ContributeForUserAsync(userId, TestContext.Current.CancellationToken);

        slices.Should().ContainSingle();
        slices[0].SectionName.Should().Be(
            Gdpr.Contracts.GdprExportSections.RoleAssignments);
    }

    private Task SeedUserAsync(Guid userId, string displayName)
    {
        SeedUser(userId, displayName);
        return Task.CompletedTask;
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

        AuthDb.RoleAssignments.Add(assignment);

        await AuthDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        return assignment;
    }
}
