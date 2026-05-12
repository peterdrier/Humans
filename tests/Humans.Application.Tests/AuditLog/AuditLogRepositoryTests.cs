using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.AuditLog;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Xunit;

namespace Humans.Application.Tests.AuditLog;

/// <summary>
/// Happy-path tests for <see cref="AuditLogRepository"/> at the repository
/// boundary — exercises the real EF implementation against an in-memory
/// database via <see cref="TestDbContextFactory"/>.
///
/// The broader <c>AuditLogServiceTests</c> covers most query paths through
/// the service → repo integration. These tests are here to satisfy the
/// 1-to-1 production-class-to-test-file mapping invariant and to document
/// <see cref="AuditLogRepository"/> as the sole writer of
/// <c>ctx.AuditLogEntries</c>.
/// </summary>
public class AuditLogRepositoryTests
{
    private readonly IDbContextFactory<HumansDbContext> _factory;
    private readonly AuditLogRepository _sut;

    public AuditLogRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _factory = new TestDbContextFactory(options);
        _sut = new AuditLogRepository(_factory);
    }

    [HumansFact]
    public async Task AddAsync_PersistsEntry_VisibleOnNextRead()
    {
        var entry = MakeEntry(AuditAction.VolunteerApproved, "User", Guid.NewGuid());

        await _sut.AddAsync(entry);

        // Round-trip via a fresh context confirms the row was saved by AddAsync.
        await using var ctx = await _factory.CreateDbContextAsync();
        var stored = await ctx.AuditLogEntries.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == entry.Id);

        stored.Should().NotBeNull(
            because: "AddAsync is the sole write path — it must persist immediately without a caller SaveChanges");
        stored!.Action.Should().Be(entry.Action);
        stored.EntityType.Should().Be(entry.EntityType);
        stored.EntityId.Should().Be(entry.EntityId);
    }

    [HumansFact]
    public async Task GetRecentAsync_ReturnsEntriesOrderedByOccurredAtDesc()
    {
        var now = Instant.FromUtc(2026, 5, 12, 10, 0);

        await _sut.AddAsync(MakeEntry(AuditAction.VolunteerApproved, "User", Guid.NewGuid(),
            occurredAt: now - Duration.FromHours(2)));
        await _sut.AddAsync(MakeEntry(AuditAction.MemberSuspended, "User", Guid.NewGuid(),
            occurredAt: now - Duration.FromHours(1)));
        var newest = MakeEntry(AuditAction.RoleAssigned, "User", Guid.NewGuid(), occurredAt: now);
        await _sut.AddAsync(newest);

        var result = await _sut.GetRecentAsync(count: 2);

        result.Should().HaveCount(2);
        result[0].Id.Should().Be(newest.Id,
            because: "GetRecentAsync returns the most recent entries first");
    }

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
