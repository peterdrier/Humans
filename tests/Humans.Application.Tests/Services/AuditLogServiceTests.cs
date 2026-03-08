using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
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
            AuditAction.VolunteerApproved, "User", entityId,
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
            AuditAction.MemberSuspended, "User", entityId,
            "Suspended for inactivity", actorId, "Admin User");

        await _dbContext.SaveChangesAsync();

        var entry = _dbContext.AuditLogEntries.Single();
        entry.ActorUserId.Should().Be(actorId);
        entry.ActorName.Should().Be("Admin User");
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
            AuditAction.RoleAssigned, "User", entityId,
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
}
