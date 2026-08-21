using Humans.AuditLog.Data;
using Humans.AuditLog.Domain;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.AuditLog.Contracts;

namespace Humans.AuditLog.Tests;

/// <summary>
/// Happy-path tests for <see cref="AuditLogRepository"/> at the repository
/// boundary — exercises the real EF implementation against an in-memory
/// database via <see cref="TestDbContextFactory{TContext}"/>.
///
/// The broader <c>AuditLogServiceTests</c> covers most query paths through
/// the service → repo integration. These tests are here to satisfy the
/// 1-to-1 production-class-to-test-file mapping invariant and to document
/// <see cref="AuditLogRepository"/> as the sole writer of
/// <c>ctx.AuditLogEntries</c>.
/// </summary>
public class AuditLogRepositoryTests
{
    private readonly IDbContextFactory<AuditLogDbContext> _factory;
    private readonly AuditLogRepository _sut;

    public AuditLogRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AuditLogDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _factory = new TestDbContextFactory<AuditLogDbContext>(options);
        _sut = new AuditLogRepository(_factory);
    }

    [HumansFact]
    public async Task AddAsync_PersistsEntry_VisibleOnNextRead()
    {
        var entry = MakeEntry(AuditAction.VolunteerApproved, "User", Guid.NewGuid());

        await _sut.AddAsync(entry, Xunit.TestContext.Current.CancellationToken);

        // Round-trip via a fresh context confirms the row was saved by AddAsync.
        await using var ctx = await _factory.CreateDbContextAsync(Xunit.TestContext.Current.CancellationToken);
        var stored = await ctx.AuditLogEntries.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == entry.Id, Xunit.TestContext.Current.CancellationToken);

        stored.Should().NotBeNull(
            because: "AddAsync is the sole write path — it must persist immediately without a caller SaveChanges");
        stored.Action.Should().Be(entry.Action);
        stored.EntityType.Should().Be(entry.EntityType);
        stored.EntityId.Should().Be(entry.EntityId);
    }

    [HumansFact]
    public async Task GetRecentAsync_ReturnsEntriesOrderedByOccurredAtDesc()
    {
        var now = Instant.FromUtc(2026, 5, 12, 10, 0);

        await _sut.AddAsync(MakeEntry(AuditAction.VolunteerApproved, "User", Guid.NewGuid(),
            occurredAt: now - Duration.FromHours(2)), Xunit.TestContext.Current.CancellationToken);
        await _sut.AddAsync(MakeEntry(AuditAction.MemberSuspended, "User", Guid.NewGuid(),
            occurredAt: now - Duration.FromHours(1)), Xunit.TestContext.Current.CancellationToken);
        var newest = MakeEntry(AuditAction.RoleAssigned, "User", Guid.NewGuid(), occurredAt: now);
        await _sut.AddAsync(newest, Xunit.TestContext.Current.CancellationToken);

        var result = await _sut.GetRecentAsync(count: 2, ct: Xunit.TestContext.Current.CancellationToken);

        result.Should().HaveCount(2);
        result[0].Id.Should().Be(newest.Id,
            because: "GetRecentAsync returns the most recent entries first");
    }

    [HumansFact]
    public async Task GetLegacyGoogleSyncEntriesAsync_ReturnsOnlyResourceRows_Oldest_First()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var now = Instant.FromUtc(2026, 5, 12, 10, 0);

        await _sut.AddAsync(MakeEntry(AuditAction.VolunteerApproved, "User", Guid.NewGuid()), ct);
        var newer = MakeGoogleSyncEntry(now);
        var older = MakeGoogleSyncEntry(now - Duration.FromHours(3));
        await _sut.AddAsync(newer, ct);
        await _sut.AddAsync(older, ct);

        var result = await _sut.GetLegacyGoogleSyncEntriesAsync(ct);

        // Chronological, and the non-Google row is absent — the one-time migration reads the whole set.
        result.Select(e => e.Id).Should().Equal([older.Id, newer.Id]);
    }

    private static AuditLogEntry MakeGoogleSyncEntry(Instant occurredAt) => new()
    {
        Id = Guid.NewGuid(),
        Action = AuditAction.GoogleResourceAccessGranted,
        EntityType = "GoogleResource",
        EntityId = Guid.NewGuid(),
        Description = "GoogleWorkspaceSyncService: Granted Drive access",
        OccurredAt = occurredAt,
        ResourceId = Guid.NewGuid(),
        Success = true,
        Role = "writer",
        SyncSource = Humans.Base.Enums.GoogleSyncSource.ManualSync,
        UserEmail = "a@b.org"
    };

    private static AuditLogEntry MakeEntry(
        AuditAction action, string entityType, Guid entityId,
        Instant? occurredAt = null) => new()
        {
            Id = Guid.NewGuid(),
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Description = $"Test {action}",
            OccurredAt = occurredAt ?? Instant.FromUtc(2026, 5, 12, 0, 0)
        };
}
