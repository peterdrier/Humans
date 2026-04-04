using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Xunit;

namespace Humans.Application.Tests.Services;

public class AuditLogServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly AuditLogService _service;

    public AuditLogServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));
        _service = new AuditLogService(_dbContext, _clock, NullLogger<AuditLogService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task LogAsync_JobOverload_AddsEntryToChangeTracker()
    {
        var entityId = Guid.NewGuid();

        await _service.LogAsync(
            AuditAction.VolunteerApproved, nameof(User), entityId,
            "Auto-approved", "SystemTeamSyncJob");

        _dbContext.ChangeTracker.Entries<Domain.Entities.AuditLogEntry>().Should().HaveCount(1);
        var tracked = _dbContext.ChangeTracker.Entries<Domain.Entities.AuditLogEntry>().Single();
        tracked.State.Should().Be(EntityState.Added);
        // Not saved yet
        _dbContext.AuditLogEntries.Count().Should().Be(0);
    }

    [Fact]
    public async Task LogAsync_HumanOverload_AddsEntryWithActorFields()
    {
        var entityId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        await _service.LogAsync(
            AuditAction.MemberSuspended, nameof(User), entityId,
            "Suspended for inactivity", actorId);

        await _dbContext.SaveChangesAsync();

        var entry = _dbContext.AuditLogEntries.Single();
        entry.ActorUserId.Should().Be(actorId);
        entry.Action.Should().Be(AuditAction.MemberSuspended);
        entry.EntityType.Should().Be("User");
        entry.EntityId.Should().Be(entityId);
        entry.Description.Should().Be("Suspended for inactivity");
        entry.OccurredAt.Should().Be(_clock.GetCurrentInstant());
    }

    [Fact]
    public async Task LogAsync_DoesNotCallSaveChanges()
    {
        var entityId = Guid.NewGuid();

        await _service.LogAsync(
            AuditAction.RoleAssigned, nameof(User), entityId,
            "Assigned Board role", "TestJob");

        _dbContext.AuditLogEntries.Count().Should().Be(0);

        await _dbContext.SaveChangesAsync();

        _dbContext.AuditLogEntries.Count().Should().Be(1);
    }

    [Fact]
    public async Task LogGoogleSyncAsync_AddsEntryWithSyncFields()
    {
        var resourceId = Guid.NewGuid();
        var relatedId = Guid.NewGuid();

        await _service.LogGoogleSyncAsync(
            AuditAction.GoogleResourceAccessGranted,
            resourceId,
            "Granted access to folder",
            "SystemTeamSyncJob",
            "user@example.com",
            "writer",
            GoogleSyncSource.SystemTeamSync,
            success: true,
            relatedEntityId: relatedId,
            relatedEntityType: "User");

        await _dbContext.SaveChangesAsync();

        var entry = _dbContext.AuditLogEntries.Single();
        entry.ResourceId.Should().Be(resourceId);
        entry.Role.Should().Be("writer");
        entry.SyncSource.Should().Be(GoogleSyncSource.SystemTeamSync);
        entry.Success.Should().Be(true);
        entry.UserEmail.Should().Be("user@example.com");
        entry.EntityType.Should().Be("GoogleResource");
        entry.RelatedEntityId.Should().Be(relatedId);
        entry.RelatedEntityType.Should().Be("User");
    }

    // ===== GetByResourceAsync =====

    [Fact]
    public async Task GetByResourceAsync_ReturnsEntriesForResource_OrderedByOccurredAtDesc()
    {
        var resourceId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        var older = SeedAuditLogEntry(AuditAction.VolunteerApproved, "User", Guid.NewGuid(),
            now - Duration.FromHours(2), resourceId: resourceId);
        var newer = SeedAuditLogEntry(AuditAction.MemberSuspended, "User", Guid.NewGuid(),
            now - Duration.FromHours(1), resourceId: resourceId);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetByResourceAsync(resourceId);

        result.Should().HaveCount(2);
        result[0].Id.Should().Be(newer.Id);
        result[1].Id.Should().Be(older.Id);
    }

    [Fact]
    public async Task GetByResourceAsync_LimitsTo200()
    {
        var resourceId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        for (var i = 0; i < 201; i++)
        {
            SeedAuditLogEntry(AuditAction.VolunteerApproved, "User", Guid.NewGuid(),
                now - Duration.FromMinutes(i), resourceId: resourceId);
        }
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetByResourceAsync(resourceId);

        result.Should().HaveCount(200);
    }

    [Fact]
    public async Task GetByResourceAsync_ReturnsEmptyForNonExistentResource()
    {
        var result = await _service.GetByResourceAsync(Guid.NewGuid());

        result.Should().BeEmpty();
    }

    // ===== GetGoogleSyncByUserAsync =====

    [Fact]
    public async Task GetGoogleSyncByUserAsync_ReturnsEntriesWithResourceAndRelatedEntity()
    {
        var userId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        _dbContext.GoogleResources.Add(new GoogleResource
        {
            Id = resourceId,
            TeamId = Guid.NewGuid(),
            GoogleId = "google-123",
            Name = "Test Resource",
            ResourceType = GoogleResourceType.DriveFolder,
            IsActive = true
        });
        SeedAuditLogEntry(AuditAction.GoogleResourceAccessGranted, "GoogleResource", resourceId,
            _clock.GetCurrentInstant(), resourceId: resourceId, relatedEntityId: userId);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetGoogleSyncByUserAsync(userId);

        result.Should().HaveCount(1);
        result[0].ResourceId.Should().Be(resourceId);
        result[0].RelatedEntityId.Should().Be(userId);
        result[0].Resource.Should().NotBeNull();
        result[0].Resource!.Name.Should().Be("Test Resource");
    }

    [Fact]
    public async Task GetGoogleSyncByUserAsync_ExcludesEntriesWithoutResourceId()
    {
        var userId = Guid.NewGuid();
        SeedAuditLogEntry(AuditAction.VolunteerApproved, "User", Guid.NewGuid(),
            _clock.GetCurrentInstant(), resourceId: null, relatedEntityId: userId);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetGoogleSyncByUserAsync(userId);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetGoogleSyncByUserAsync_ReturnsEmptyWhenNoSyncEntries()
    {
        var result = await _service.GetGoogleSyncByUserAsync(Guid.NewGuid());

        result.Should().BeEmpty();
    }

    // ===== GetRecentAsync =====

    [Fact]
    public async Task GetRecentAsync_ReturnsTopN_OrderedByOccurredAtDesc()
    {
        var now = _clock.GetCurrentInstant();
        SeedAuditLogEntry(AuditAction.VolunteerApproved, "User", Guid.NewGuid(), now - Duration.FromHours(4));
        SeedAuditLogEntry(AuditAction.MemberSuspended, "User", Guid.NewGuid(), now - Duration.FromHours(3));
        var third = SeedAuditLogEntry(AuditAction.RoleAssigned, "User", Guid.NewGuid(), now - Duration.FromHours(2));
        var fourth = SeedAuditLogEntry(AuditAction.RoleEnded, "User", Guid.NewGuid(), now - Duration.FromHours(1));
        var fifth = SeedAuditLogEntry(AuditAction.VolunteerApproved, "User", Guid.NewGuid(), now);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetRecentAsync(3);

        result.Should().HaveCount(3);
        result[0].Id.Should().Be(fifth.Id);
        result[1].Id.Should().Be(fourth.Id);
        result[2].Id.Should().Be(third.Id);
    }

    [Fact]
    public async Task GetRecentAsync_RespectsCountParameter()
    {
        var now = _clock.GetCurrentInstant();
        SeedAuditLogEntry(AuditAction.VolunteerApproved, "User", Guid.NewGuid(), now - Duration.FromHours(2));
        SeedAuditLogEntry(AuditAction.MemberSuspended, "User", Guid.NewGuid(), now - Duration.FromHours(1));
        var mostRecent = SeedAuditLogEntry(AuditAction.RoleAssigned, "User", Guid.NewGuid(), now);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetRecentAsync(1);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be(mostRecent.Id);
    }

    [Fact]
    public async Task GetRecentAsync_ReturnsEmptyWhenNoEntries()
    {
        var result = await _service.GetRecentAsync(10);

        result.Should().BeEmpty();
    }

    // ===== GetFilteredAsync =====

    [Fact]
    public async Task GetFilteredAsync_NoFilter_ReturnsAllWithCorrectTotalCount()
    {
        var now = _clock.GetCurrentInstant();
        SeedAuditLogEntry(AuditAction.VolunteerApproved, "User", Guid.NewGuid(), now - Duration.FromHours(2));
        SeedAuditLogEntry(AuditAction.MemberSuspended, "User", Guid.NewGuid(), now - Duration.FromHours(1));
        SeedAuditLogEntry(AuditAction.RoleAssigned, "User", Guid.NewGuid(), now);
        await _dbContext.SaveChangesAsync();

        var (items, totalCount, _) = await _service.GetFilteredAsync(null, 1, 10);

        items.Should().HaveCount(3);
        totalCount.Should().Be(3);
    }

    [Fact]
    public async Task GetFilteredAsync_FiltersByAction()
    {
        var now = _clock.GetCurrentInstant();
        SeedAuditLogEntry(AuditAction.VolunteerApproved, "User", Guid.NewGuid(), now - Duration.FromHours(2));
        SeedAuditLogEntry(AuditAction.MemberSuspended, "User", Guid.NewGuid(), now - Duration.FromHours(1));
        SeedAuditLogEntry(AuditAction.VolunteerApproved, "User", Guid.NewGuid(), now);
        await _dbContext.SaveChangesAsync();

        var (items, totalCount, _) = await _service.GetFilteredAsync("VolunteerApproved", 1, 10);

        items.Should().HaveCount(2);
        totalCount.Should().Be(2);
        items.Should().OnlyContain(e => e.Action == AuditAction.VolunteerApproved);
    }

    [Fact]
    public async Task GetFilteredAsync_ReturnsAnomalyCount()
    {
        var now = _clock.GetCurrentInstant();
        SeedAuditLogEntry(AuditAction.AnomalousPermissionDetected, "GoogleResource", Guid.NewGuid(), now);
        SeedAuditLogEntry(AuditAction.VolunteerApproved, "User", Guid.NewGuid(), now - Duration.FromHours(1));
        await _dbContext.SaveChangesAsync();

        var (items, totalCount, anomalyCount) = await _service.GetFilteredAsync("VolunteerApproved", 1, 10);

        items.Should().HaveCount(1);
        totalCount.Should().Be(1);
        anomalyCount.Should().Be(1);
    }

    [Fact]
    public async Task GetFilteredAsync_Pagination()
    {
        var now = _clock.GetCurrentInstant();
        for (var i = 0; i < 5; i++)
        {
            SeedAuditLogEntry(AuditAction.VolunteerApproved, "User", Guid.NewGuid(),
                now - Duration.FromHours(i));
        }
        await _dbContext.SaveChangesAsync();

        var (items, totalCount, _) = await _service.GetFilteredAsync(null, 2, 2);

        items.Should().HaveCount(2);
        totalCount.Should().Be(5);
    }

    // ===== GetByUserAsync =====

    [Fact]
    public async Task GetByUserAsync_MatchesEntityIdOrRelatedEntityId()
    {
        var userId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        SeedAuditLogEntry(AuditAction.VolunteerApproved, "User", userId,
            now - Duration.FromHours(1));
        SeedAuditLogEntry(AuditAction.RoleAssigned, "Team", Guid.NewGuid(),
            now, relatedEntityId: userId);
        // Entry that should NOT match
        SeedAuditLogEntry(AuditAction.MemberSuspended, "User", Guid.NewGuid(),
            now - Duration.FromHours(2));
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetByUserAsync(userId, 10);

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetByUserAsync_RespectsCountLimit()
    {
        var userId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        for (var i = 0; i < 5; i++)
        {
            SeedAuditLogEntry(AuditAction.VolunteerApproved, "User", userId,
                now - Duration.FromHours(i));
        }
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetByUserAsync(userId, 3);

        result.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetByUserAsync_OrdersByOccurredAtDesc()
    {
        var userId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        var older = SeedAuditLogEntry(AuditAction.VolunteerApproved, "User", userId,
            now - Duration.FromHours(2));
        var middle = SeedAuditLogEntry(AuditAction.MemberSuspended, "User", userId,
            now - Duration.FromHours(1));
        var newest = SeedAuditLogEntry(AuditAction.RoleAssigned, "User", userId, now);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetByUserAsync(userId, 10);

        result.Should().HaveCount(3);
        result[0].Id.Should().Be(newest.Id);
        result[1].Id.Should().Be(middle.Id);
        result[2].Id.Should().Be(older.Id);
    }

    // --- Helpers ---

    private AuditLogEntry SeedAuditLogEntry(
        AuditAction action, string entityType, Guid entityId, Instant occurredAt,
        Guid? resourceId = null, Guid? relatedEntityId = null)
    {
        var entry = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Description = $"Test {action}",
            OccurredAt = occurredAt,
            ResourceId = resourceId,
            RelatedEntityId = relatedEntityId
        };
        _dbContext.AuditLogEntries.Add(entry);
        return entry;
    }
}
