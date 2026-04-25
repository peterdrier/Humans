using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

public class CampRoleTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly CampService _service;
    private readonly IAuditLogService _auditLog;

    public CampRoleTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 13, 12, 0));
        _auditLog = Substitute.For<IAuditLogService>();

        _service = new CampService(
            _dbContext,
            _auditLog,
            Substitute.For<ISystemTeamSync>(),
            _clock,
            new MemoryCache(new MemoryCacheOptions()));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    // ==========================================================================
    // CreateCampRoleDefinitionAsync
    // ==========================================================================

    [Fact]
    public async Task CreateCampRoleDefinitionAsync_PersistsAndReturnsDto()
    {
        var actorUserId = Guid.NewGuid();

        var dto = await _service.CreateCampRoleDefinitionAsync(
            name: "Test Role",
            description: "for tests",
            slotCount: 2,
            minimumRequired: 1,
            sortOrder: 100,
            isRequired: true,
            actorUserId: actorUserId);

        dto.Name.Should().Be("Test Role");
        dto.Description.Should().Be("for tests");
        dto.SlotCount.Should().Be(2);
        dto.MinimumRequired.Should().Be(1);
        dto.SortOrder.Should().Be(100);
        dto.IsRequired.Should().BeTrue();
        dto.DeactivatedAt.Should().BeNull();

        var row = await _dbContext.CampRoleDefinitions.FirstOrDefaultAsync(d => d.Id == dto.Id);
        row.Should().NotBeNull();
        row!.Name.Should().Be("Test Role");
        row.Description.Should().Be("for tests");
        row.SlotCount.Should().Be(2);
        row.MinimumRequired.Should().Be(1);
        row.SortOrder.Should().Be(100);
        row.IsRequired.Should().BeTrue();
        row.DeactivatedAt.Should().BeNull();
        row.CreatedAt.Should().Be(_clock.GetCurrentInstant());
        row.UpdatedAt.Should().Be(_clock.GetCurrentInstant());

        await _auditLog.Received(1).LogAsync(
            AuditAction.CampRoleDefinitionCreated,
            nameof(CampRoleDefinition),
            dto.Id,
            Arg.Any<string>(),
            actorUserId,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    [Fact]
    public async Task CreateCampRoleDefinitionAsync_RejectsDuplicateName()
    {
        var actorUserId = Guid.NewGuid();
        await _service.CreateCampRoleDefinitionAsync(
            "Dup Role", null, 1, 0, 0, false, actorUserId);

        var act = () => _service.CreateCampRoleDefinitionAsync(
            "Dup Role", null, 1, 0, 0, false, actorUserId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");
    }

    // ==========================================================================
    // UpdateCampRoleDefinitionAsync
    // ==========================================================================

    [Fact]
    public async Task UpdateCampRoleDefinitionAsync_UpdatesFieldsAndUpdatedAt()
    {
        var actorUserId = Guid.NewGuid();
        var created = await _service.CreateCampRoleDefinitionAsync(
            "Original", "old desc", 1, 0, 5, false, actorUserId);

        // Advance the clock so we can verify UpdatedAt changes
        var createdAt = _clock.GetCurrentInstant();
        _clock.AdvanceMinutes(30);
        var afterAdvance = _clock.GetCurrentInstant();

        var updated = await _service.UpdateCampRoleDefinitionAsync(
            roleDefinitionId: created.Id,
            name: "Renamed",
            description: "new desc",
            slotCount: 3,
            minimumRequired: 2,
            sortOrder: 50,
            isRequired: true,
            actorUserId: actorUserId);

        updated.Name.Should().Be("Renamed");
        updated.Description.Should().Be("new desc");
        updated.SlotCount.Should().Be(3);
        updated.MinimumRequired.Should().Be(2);
        updated.SortOrder.Should().Be(50);
        updated.IsRequired.Should().BeTrue();

        var row = await _dbContext.CampRoleDefinitions.FirstAsync(d => d.Id == created.Id);
        row.Name.Should().Be("Renamed");
        row.Description.Should().Be("new desc");
        row.SlotCount.Should().Be(3);
        row.MinimumRequired.Should().Be(2);
        row.SortOrder.Should().Be(50);
        row.IsRequired.Should().BeTrue();
        row.CreatedAt.Should().Be(createdAt);
        row.UpdatedAt.Should().Be(afterAdvance);

        await _auditLog.Received(1).LogAsync(
            AuditAction.CampRoleDefinitionUpdated,
            nameof(CampRoleDefinition),
            created.Id,
            Arg.Any<string>(),
            actorUserId,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    // ==========================================================================
    // Deactivate / Reactivate
    // ==========================================================================

    [Fact]
    public async Task DeactivateCampRoleDefinitionAsync_SetsDeactivatedAt_AndReactivateClearsIt()
    {
        var actorUserId = Guid.NewGuid();
        var created = await _service.CreateCampRoleDefinitionAsync(
            "Toggle", null, 1, 0, 0, false, actorUserId);

        _clock.AdvanceMinutes(15);
        var deactivatedExpectedAt = _clock.GetCurrentInstant();

        await _service.DeactivateCampRoleDefinitionAsync(created.Id, actorUserId);

        var afterDeactivate = await _dbContext.CampRoleDefinitions.FirstAsync(d => d.Id == created.Id);
        afterDeactivate.DeactivatedAt.Should().Be(deactivatedExpectedAt);
        afterDeactivate.UpdatedAt.Should().Be(deactivatedExpectedAt);

        await _auditLog.Received(1).LogAsync(
            AuditAction.CampRoleDefinitionDeactivated,
            nameof(CampRoleDefinition),
            created.Id,
            Arg.Any<string>(),
            actorUserId,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());

        _clock.AdvanceMinutes(15);
        var reactivatedExpectedAt = _clock.GetCurrentInstant();

        await _service.ReactivateCampRoleDefinitionAsync(created.Id, actorUserId);

        var afterReactivate = await _dbContext.CampRoleDefinitions.FirstAsync(d => d.Id == created.Id);
        afterReactivate.DeactivatedAt.Should().BeNull();
        afterReactivate.UpdatedAt.Should().Be(reactivatedExpectedAt);

        await _auditLog.Received(1).LogAsync(
            AuditAction.CampRoleDefinitionReactivated,
            nameof(CampRoleDefinition),
            created.Id,
            Arg.Any<string>(),
            actorUserId,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    // ==========================================================================
    // GetCampRoleDefinitionsAsync
    // ==========================================================================

    [Fact]
    public async Task GetCampRoleDefinitionsAsync_RespectsIncludeDeactivatedFlag()
    {
        var actorUserId = Guid.NewGuid();

        // Create two locally-named roles so we can assert about them specifically
        // (the in-memory provider seeds the 6 default roles via HasData).
        var active = await _service.CreateCampRoleDefinitionAsync(
            "Active", null, 1, 0, 1000, false, actorUserId);
        var dead = await _service.CreateCampRoleDefinitionAsync(
            "Dead", null, 1, 0, 1001, false, actorUserId);

        await _service.DeactivateCampRoleDefinitionAsync(dead.Id, actorUserId);

        var withoutDeactivated = await _service.GetCampRoleDefinitionsAsync(includeDeactivated: false);
        var localWithout = withoutDeactivated.Where(d =>
            string.Equals(d.Name, "Active", StringComparison.Ordinal) ||
            string.Equals(d.Name, "Dead", StringComparison.Ordinal)).ToList();
        localWithout.Should().ContainSingle();
        localWithout[0].Name.Should().Be("Active");

        var withDeactivated = await _service.GetCampRoleDefinitionsAsync(includeDeactivated: true);
        var localWith = withDeactivated.Where(d =>
            string.Equals(d.Name, "Active", StringComparison.Ordinal) ||
            string.Equals(d.Name, "Dead", StringComparison.Ordinal)).ToList();
        localWith.Should().HaveCount(2);
        localWith.Select(d => d.Name).Should().Contain(["Active", "Dead"]);

        // Ordering: results sorted by SortOrder ascending across the full set
        withDeactivated.Select(d => d.SortOrder).Should().BeInAscendingOrder();
    }

    // ==========================================================================
    // AssignCampRoleAsync — test helpers
    // ==========================================================================

    private async Task<(Guid CampId, Guid SeasonId)> SeedCampSeasonAsync(
        string slug = "test-camp",
        CampSeasonStatus status = CampSeasonStatus.Active,
        int year = 2026)
    {
        var creatorUserId = Guid.NewGuid();
        _dbContext.Users.Add(new User
        {
            Id = creatorUserId,
            UserName = $"creator-{creatorUserId}",
            Email = $"creator-{creatorUserId}@example.com",
            DisplayName = "Test Creator",
        });
        var camp = new Camp
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            ContactEmail = "x@example.com",
            ContactPhone = "",
            CreatedByUserId = creatorUserId,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant(),
        };
        var season = new CampSeason
        {
            Id = Guid.NewGuid(),
            CampId = camp.Id,
            Year = year,
            Name = $"{slug}-{year}",
            Status = status,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant(),
        };
        _dbContext.Camps.Add(camp);
        _dbContext.CampSeasons.Add(season);
        await _dbContext.SaveChangesAsync();
        return (camp.Id, season.Id);
    }

    private async Task<(Guid UserId, Guid MemberId)> SeedCampMemberAsync(
        Guid seasonId, CampMemberStatus status = CampMemberStatus.Active)
    {
        var userId = Guid.NewGuid();
        _dbContext.Users.Add(new User
        {
            Id = userId,
            UserName = $"member-{userId}",
            Email = $"{userId}@example.com",
            DisplayName = "Test Member",
        });
        var member = new CampMember
        {
            Id = Guid.NewGuid(),
            CampSeasonId = seasonId,
            UserId = userId,
            Status = status,
            RequestedAt = _clock.GetCurrentInstant(),
            ConfirmedAt = status == CampMemberStatus.Active ? _clock.GetCurrentInstant() : (Instant?)null,
        };
        _dbContext.CampMembers.Add(member);
        await _dbContext.SaveChangesAsync();
        return (userId, member.Id);
    }

    private async Task<Guid> SeedHumanUserAsync()
    {
        var userId = Guid.NewGuid();
        _dbContext.Users.Add(new User
        {
            Id = userId,
            UserName = $"user-{userId}",
            Email = $"{userId}@example.com",
            DisplayName = "Other Human",
        });
        await _dbContext.SaveChangesAsync();
        return userId;
    }

    private async Task<CampRoleDefinition> SeedRoleDefinitionAsync(
        string name = "Test Role",
        int slotCount = 1,
        int minimumRequired = 1,
        bool isRequired = true,
        int sortOrder = 100)
    {
        var dto = await _service.CreateCampRoleDefinitionAsync(
            name, null, slotCount, minimumRequired, sortOrder, isRequired,
            actorUserId: Guid.NewGuid());
        return await _dbContext.CampRoleDefinitions.FirstAsync(d => d.Id == dto.Id);
    }

    // ==========================================================================
    // AssignCampRoleAsync
    // ==========================================================================

    [Fact]
    public async Task AssignCampRoleAsync_HappyPath_PersistsAssignmentAndReturnsAssignee()
    {
        var (_, seasonId) = await SeedCampSeasonAsync(slug: "happy-camp", year: 2026);
        var (userId, _) = await SeedCampMemberAsync(seasonId, CampMemberStatus.Active);
        var role = await SeedRoleDefinitionAsync("Happy Role", slotCount: 1);
        var assignedBy = Guid.NewGuid();

        var result = await _service.AssignCampRoleAsync(
            campSeasonId: seasonId,
            campRoleDefinitionId: role.Id,
            slotIndex: 0,
            assigneeUserId: userId,
            assignedByUserId: assignedBy,
            autoPromoteToMember: false);

        result.Outcome.Should().Be(AssignCampRoleOutcome.Assigned);
        result.AssigneeUserId.Should().Be(userId);
        result.RoleName.Should().Be("Happy Role");
        result.CampSlug.Should().Be("happy-camp");
        result.CampName.Should().Be("happy-camp-2026");
        result.AssignmentId.Should().NotBe(Guid.Empty);

        var row = await _dbContext.CampRoleAssignments.FirstOrDefaultAsync(a => a.Id == result.AssignmentId);
        row.Should().NotBeNull();
        row!.CampRoleDefinitionId.Should().Be(role.Id);
        row.CampSeasonId.Should().Be(seasonId);
        row.SlotIndex.Should().Be(0);
        row.AssignedByUserId.Should().Be(assignedBy);
        row.AssignedAt.Should().Be(_clock.GetCurrentInstant());

        await _auditLog.Received(1).LogAsync(
            AuditAction.CampRoleAssigned,
            nameof(CampRoleAssignment),
            row.Id,
            Arg.Any<string>(),
            assignedBy,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    [Fact]
    public async Task AssignCampRoleAsync_RejectsOccupiedSlot()
    {
        var (_, seasonId) = await SeedCampSeasonAsync();
        var (firstUserId, _) = await SeedCampMemberAsync(seasonId, CampMemberStatus.Active);
        var (secondUserId, _) = await SeedCampMemberAsync(seasonId, CampMemberStatus.Active);
        var role = await SeedRoleDefinitionAsync("Solo Role", slotCount: 1);
        var assignedBy = Guid.NewGuid();

        var first = await _service.AssignCampRoleAsync(
            seasonId, role.Id, slotIndex: 0,
            assigneeUserId: firstUserId, assignedByUserId: assignedBy, autoPromoteToMember: false);
        first.Outcome.Should().Be(AssignCampRoleOutcome.Assigned);

        var second = await _service.AssignCampRoleAsync(
            seasonId, role.Id, slotIndex: 0,
            assigneeUserId: secondUserId, assignedByUserId: assignedBy, autoPromoteToMember: false);

        second.Outcome.Should().Be(AssignCampRoleOutcome.SlotOccupied);
        second.AssignmentId.Should().Be(Guid.Empty);

        // Only the first assignment should be persisted.
        var assignments = await _dbContext.CampRoleAssignments
            .Where(a => a.CampSeasonId == seasonId && a.CampRoleDefinitionId == role.Id)
            .ToListAsync();
        assignments.Should().ContainSingle();
    }

    [Fact]
    public async Task AssignCampRoleAsync_RejectsSameMemberInDifferentSlotsOfSameRole()
    {
        var (_, seasonId) = await SeedCampSeasonAsync();
        var (userId, _) = await SeedCampMemberAsync(seasonId, CampMemberStatus.Active);
        var role = await SeedRoleDefinitionAsync("Two-slot Role", slotCount: 2, minimumRequired: 1);
        var assignedBy = Guid.NewGuid();

        var first = await _service.AssignCampRoleAsync(
            seasonId, role.Id, slotIndex: 0,
            assigneeUserId: userId, assignedByUserId: assignedBy, autoPromoteToMember: false);
        first.Outcome.Should().Be(AssignCampRoleOutcome.Assigned);

        var second = await _service.AssignCampRoleAsync(
            seasonId, role.Id, slotIndex: 1,
            assigneeUserId: userId, assignedByUserId: assignedBy, autoPromoteToMember: false);

        second.Outcome.Should().Be(AssignCampRoleOutcome.AlreadyHoldsRole);
        second.AssignmentId.Should().Be(Guid.Empty);

        var assignments = await _dbContext.CampRoleAssignments
            .Where(a => a.CampSeasonId == seasonId && a.CampRoleDefinitionId == role.Id)
            .ToListAsync();
        assignments.Should().ContainSingle();
    }

    [Fact]
    public async Task AssignCampRoleAsync_AutoPromotesNonMember()
    {
        var (_, seasonId) = await SeedCampSeasonAsync(slug: "auto-camp", year: 2026);
        var userId = await SeedHumanUserAsync();
        var role = await SeedRoleDefinitionAsync("Auto Role", slotCount: 1);
        var assignedBy = Guid.NewGuid();

        var result = await _service.AssignCampRoleAsync(
            seasonId, role.Id, slotIndex: 0,
            assigneeUserId: userId, assignedByUserId: assignedBy, autoPromoteToMember: true);

        result.Outcome.Should().Be(AssignCampRoleOutcome.AssignedWithAutoPromote);
        result.AssigneeUserId.Should().Be(userId);
        result.RoleName.Should().Be("Auto Role");

        var member = await _dbContext.CampMembers
            .FirstOrDefaultAsync(m => m.CampSeasonId == seasonId && m.UserId == userId);
        member.Should().NotBeNull();
        member!.Status.Should().Be(CampMemberStatus.Active);
        member.ConfirmedAt.Should().Be(_clock.GetCurrentInstant());
        member.ConfirmedByUserId.Should().Be(assignedBy);

        // Both audit calls fired: membership approval + role assignment.
        await _auditLog.Received(1).LogAsync(
            AuditAction.CampMemberApproved,
            nameof(CampMember),
            member.Id,
            Arg.Any<string>(),
            assignedBy,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());

        await _auditLog.Received(1).LogAsync(
            AuditAction.CampRoleAssigned,
            nameof(CampRoleAssignment),
            result.AssignmentId,
            Arg.Any<string>(),
            assignedBy,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    [Fact]
    public async Task AssignCampRoleAsync_WithoutAutoPromote_RejectsNonMember()
    {
        var (_, seasonId) = await SeedCampSeasonAsync();
        var userId = await SeedHumanUserAsync();
        var role = await SeedRoleDefinitionAsync("No-Auto Role", slotCount: 1);
        var assignedBy = Guid.NewGuid();

        var result = await _service.AssignCampRoleAsync(
            seasonId, role.Id, slotIndex: 0,
            assigneeUserId: userId, assignedByUserId: assignedBy, autoPromoteToMember: false);

        result.Outcome.Should().Be(AssignCampRoleOutcome.InvalidUser);
        result.AssignmentId.Should().Be(Guid.Empty);

        var member = await _dbContext.CampMembers
            .FirstOrDefaultAsync(m => m.CampSeasonId == seasonId && m.UserId == userId);
        member.Should().BeNull();

        var assignments = await _dbContext.CampRoleAssignments
            .Where(a => a.CampSeasonId == seasonId)
            .ToListAsync();
        assignments.Should().BeEmpty();

        // No audit calls should have been made for an outright rejection.
        await _auditLog.DidNotReceive().LogAsync(
            AuditAction.CampRoleAssigned,
            Arg.Any<string>(),
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<Guid>(),
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    [Fact]
    public async Task AssignCampRoleAsync_AutoPromotesPendingMember()
    {
        var (_, seasonId) = await SeedCampSeasonAsync();
        var (userId, memberId) = await SeedCampMemberAsync(seasonId, CampMemberStatus.Pending);
        var role = await SeedRoleDefinitionAsync("Pending Auto Role", slotCount: 1);
        var assignedBy = Guid.NewGuid();

        var result = await _service.AssignCampRoleAsync(
            seasonId, role.Id, slotIndex: 0,
            assigneeUserId: userId, assignedByUserId: assignedBy, autoPromoteToMember: true);

        result.Outcome.Should().Be(AssignCampRoleOutcome.AssignedWithAutoPromote);

        var member = await _dbContext.CampMembers.FirstAsync(m => m.Id == memberId);
        member.Status.Should().Be(CampMemberStatus.Active);
        member.ConfirmedAt.Should().Be(_clock.GetCurrentInstant());
        member.ConfirmedByUserId.Should().Be(assignedBy);

        await _auditLog.Received(1).LogAsync(
            AuditAction.CampMemberApproved,
            nameof(CampMember),
            memberId,
            Arg.Any<string>(),
            assignedBy,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());

        await _auditLog.Received(1).LogAsync(
            AuditAction.CampRoleAssigned,
            nameof(CampRoleAssignment),
            result.AssignmentId,
            Arg.Any<string>(),
            assignedBy,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    // ==========================================================================
    // UnassignCampRoleAsync
    // ==========================================================================

    [Fact]
    public async Task UnassignCampRoleAsync_DeletesRow_AndWritesAudit()
    {
        var (_, seasonId) = await SeedCampSeasonAsync();
        var (userId, _) = await SeedCampMemberAsync(seasonId, CampMemberStatus.Active);
        var role = await SeedRoleDefinitionAsync("Unassign Role", slotCount: 1);
        var assignedBy = Guid.NewGuid();

        var assignResult = await _service.AssignCampRoleAsync(
            seasonId, role.Id, slotIndex: 0,
            assigneeUserId: userId, assignedByUserId: assignedBy, autoPromoteToMember: false);
        assignResult.Outcome.Should().Be(AssignCampRoleOutcome.Assigned);

        var actorUserId = Guid.NewGuid();
        await _service.UnassignCampRoleAsync(assignResult.AssignmentId, actorUserId);

        var row = await _dbContext.CampRoleAssignments.FirstOrDefaultAsync(a => a.Id == assignResult.AssignmentId);
        row.Should().BeNull();

        await _auditLog.Received(1).LogAsync(
            AuditAction.CampRoleUnassigned,
            nameof(CampRoleAssignment),
            assignResult.AssignmentId,
            Arg.Any<string>(),
            actorUserId,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    // ==========================================================================
    // GetCampRoleAssignmentsAsync
    // ==========================================================================

    [Fact]
    public async Task GetCampRoleAssignmentsAsync_OrdersBySortOrderThenSlotIndex()
    {
        var (_, seasonId) = await SeedCampSeasonAsync();
        var (userId, _) = await SeedCampMemberAsync(seasonId, CampMemberStatus.Active);
        var assignedBy = Guid.NewGuid();

        // Two roles with different SortOrder values; assign the same active member to both.
        var roleHigh = await SeedRoleDefinitionAsync("Role-High-Sort", slotCount: 1, sortOrder: 200);
        var roleLow = await SeedRoleDefinitionAsync("Role-Low-Sort", slotCount: 1, sortOrder: 50);

        // Assign in arbitrary (high-then-low) order so we can verify sorting kicks in.
        var assignHigh = await _service.AssignCampRoleAsync(
            seasonId, roleHigh.Id, slotIndex: 0,
            assigneeUserId: userId, assignedByUserId: assignedBy, autoPromoteToMember: false);
        assignHigh.Outcome.Should().Be(AssignCampRoleOutcome.Assigned);

        var assignLow = await _service.AssignCampRoleAsync(
            seasonId, roleLow.Id, slotIndex: 0,
            assigneeUserId: userId, assignedByUserId: assignedBy, autoPromoteToMember: false);
        assignLow.Outcome.Should().Be(AssignCampRoleOutcome.Assigned);

        var result = await _service.GetCampRoleAssignmentsAsync(seasonId);
        result.Should().HaveCount(2);
        result[0].RoleName.Should().Be("Role-Low-Sort");
        result[1].RoleName.Should().Be("Role-High-Sort");
        result[0].AssigneeUserId.Should().Be(userId);
        result[0].AssigneeDisplayName.Should().Be("Test Member");
        result[0].SlotIndex.Should().Be(0);
    }

    // ==========================================================================
    // Cascade hooks
    // ==========================================================================

    [Fact]
    public async Task RemovingCampMember_DeletesAllTheirRoleAssignments()
    {
        var (_, seasonId) = await SeedCampSeasonAsync();
        var (userId, memberId) = await SeedCampMemberAsync(seasonId, CampMemberStatus.Active);
        var role = await SeedRoleDefinitionAsync("Cascade-Member Role", slotCount: 1);
        var assignedBy = Guid.NewGuid();

        var assignResult = await _service.AssignCampRoleAsync(
            seasonId, role.Id, slotIndex: 0,
            assigneeUserId: userId, assignedByUserId: assignedBy, autoPromoteToMember: false);
        assignResult.Outcome.Should().Be(AssignCampRoleOutcome.Assigned);

        var removedBy = Guid.NewGuid();
        await _service.RemoveCampMemberAsync(memberId, removedBy);

        var roleRow = await _dbContext.CampRoleAssignments.FirstOrDefaultAsync(a => a.Id == assignResult.AssignmentId);
        roleRow.Should().BeNull();

        await _auditLog.Received(1).LogAsync(
            AuditAction.CampRoleUnassigned,
            nameof(CampRoleAssignment),
            assignResult.AssignmentId,
            Arg.Any<string>(),
            removedBy,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    [Theory]
    [InlineData(CampSeasonStatus.Rejected)]
    [InlineData(CampSeasonStatus.Withdrawn)]
    public async Task SeasonStatusChange_ToRejectedOrWithdrawn_DeletesAllAssignmentsForSeason(CampSeasonStatus terminal)
    {
        // Rejecting a season requires Pending; withdrawing requires Pending or Active.
        // Seed in a state that supports both, so we can branch later.
        var seasonStartStatus = terminal == CampSeasonStatus.Rejected
            ? CampSeasonStatus.Pending
            : CampSeasonStatus.Active;
        var (_, seasonId) = await SeedCampSeasonAsync(slug: $"cascade-{terminal}", status: seasonStartStatus);

        // Role assignment requires the season to be Active or Full at assign time.
        // For the Rejected branch we need to flip the season to Active just for assignment, then back to Pending.
        var season = await _dbContext.CampSeasons.FirstAsync(s => s.Id == seasonId);
        season.Status = CampSeasonStatus.Active;
        await _dbContext.SaveChangesAsync();

        var (userId, _) = await SeedCampMemberAsync(seasonId, CampMemberStatus.Active);
        var role = await SeedRoleDefinitionAsync($"Cascade-{terminal}-Role", slotCount: 1);
        var assignedBy = Guid.NewGuid();

        var assignResult = await _service.AssignCampRoleAsync(
            seasonId, role.Id, slotIndex: 0,
            assigneeUserId: userId, assignedByUserId: assignedBy, autoPromoteToMember: false);
        assignResult.Outcome.Should().Be(AssignCampRoleOutcome.Assigned);

        // Reset season back to Pending for the Reject branch (Withdraw is fine on Active).
        if (terminal == CampSeasonStatus.Rejected)
        {
            season.Status = CampSeasonStatus.Pending;
            await _dbContext.SaveChangesAsync();
        }

        if (terminal == CampSeasonStatus.Rejected)
        {
            await _service.RejectSeasonAsync(seasonId, Guid.NewGuid(), "no good");
        }
        else
        {
            await _service.WithdrawSeasonAsync(seasonId);
        }

        var roleRow = await _dbContext.CampRoleAssignments.FirstOrDefaultAsync(a => a.Id == assignResult.AssignmentId);
        roleRow.Should().BeNull();
    }
}
