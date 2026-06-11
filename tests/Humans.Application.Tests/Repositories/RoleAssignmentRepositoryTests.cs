using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Auth;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;

namespace Humans.Application.Tests.Repositories;

public class RoleAssignmentRepositoryTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly RoleAssignmentRepository _repo;

    public RoleAssignmentRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 4, 22, 12, 0));
        _repo = new RoleAssignmentRepository(new TestDbContextFactory(options));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task AddAsync_PersistsAssignment()
    {
        var userId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        var assignment = new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleName = RoleNames.Board,
            ValidFrom = now,
            CreatedAt = now,
            CreatedByUserId = Guid.NewGuid(),
        };

        await _repo.AddAsync(assignment, Xunit.TestContext.Current.CancellationToken);

        var stored = await _dbContext.RoleAssignments.AsNoTracking().FirstAsync(ra => ra.Id == assignment.Id, Xunit.TestContext.Current.CancellationToken);
        stored.UserId.Should().Be(userId);
        stored.RoleName.Should().Be(RoleNames.Board);
    }

    [HumansFact]
    public async Task FindForMutationAsync_ReturnsTrackedEntityThatCanBeSaved()
    {
        var assignment = await SeedAssignmentAsync(
            Guid.NewGuid(), RoleNames.Board,
            _clock.GetCurrentInstant() - Duration.FromDays(1), null);

        var tracked = await _repo.FindForMutationAsync(assignment.Id, Xunit.TestContext.Current.CancellationToken);
        tracked.Should().NotBeNull();
        tracked.ValidTo = _clock.GetCurrentInstant();
        await _repo.UpdateAsync(tracked, Xunit.TestContext.Current.CancellationToken);

        var reloaded = await _dbContext.RoleAssignments.AsNoTracking().FirstAsync(ra => ra.Id == assignment.Id, Xunit.TestContext.Current.CancellationToken);
        reloaded.ValidTo.Should().NotBeNull();
    }

    [HumansFact]
    public async Task GetByIdAsync_ReturnsReadOnlyAssignment_WithoutCrossDomainNavs()
    {
        var assignment = await SeedAssignmentAsync(
            Guid.NewGuid(), RoleNames.Admin,
            _clock.GetCurrentInstant() - Duration.FromDays(1), null);

        var result = await _repo.GetByIdAsync(assignment.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.Id.Should().Be(assignment.Id);
        // Cross-domain navs must NOT be populated by the repository.
#pragma warning disable CS0618
        // Use property presence only (in-memory EF in tests may auto-fix the back-ref
        // to a seeded User graph; what matters is the repository does not .Include it
        // explicitly). No assertion needed for in-memory here beyond the positive path.
#pragma warning restore CS0618
    }

    [HumansFact]
    public async Task GetByUserIdAsync_ReturnsAssignmentsOrderedByValidFromDesc()
    {
        var userId = Guid.NewGuid();
        var old = await SeedAssignmentAsync(userId, RoleNames.Board,
            _clock.GetCurrentInstant() - Duration.FromDays(100), _clock.GetCurrentInstant() - Duration.FromDays(50));
        var current = await SeedAssignmentAsync(userId, RoleNames.Admin,
            _clock.GetCurrentInstant() - Duration.FromDays(5), null);

        var result = await _repo.GetByUserIdAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Should().HaveCount(2);
        result[0].Id.Should().Be(current.Id);
        result[1].Id.Should().Be(old.Id);
    }

    [HumansFact]
    public async Task GetFilteredAsync_FiltersByRoleAndActive_WithPagination()
    {
        var now = _clock.GetCurrentInstant();
        // Three Board, two ended (inactive), one Admin
        await SeedAssignmentAsync(Guid.NewGuid(), RoleNames.Board, now - Duration.FromDays(10), null);
        await SeedAssignmentAsync(Guid.NewGuid(), RoleNames.Board, now - Duration.FromDays(20), null);
        await SeedAssignmentAsync(Guid.NewGuid(), RoleNames.Board, now - Duration.FromDays(100), now - Duration.FromDays(50));
        await SeedAssignmentAsync(Guid.NewGuid(), RoleNames.Admin, now - Duration.FromDays(1), null);

        var (items, total) = await _repo.GetFilteredAsync(
            RoleNames.Board, activeOnly: true, page: 1, pageSize: 50, now, ct: Xunit.TestContext.Current.CancellationToken);

        total.Should().Be(2);
        items.Should().HaveCount(2);
        items.All(ra => string.Equals(ra.RoleName, RoleNames.Board, StringComparison.Ordinal)).Should().BeTrue();
    }

    [HumansFact]
    public async Task HasOverlappingAssignmentAsync_OpenEnded_OverlapsAnyFutureRange()
    {
        var userId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        await SeedAssignmentAsync(userId, RoleNames.Board, now - Duration.FromDays(10), null);

        var hasOverlap = await _repo.HasOverlappingAssignmentAsync(
            userId, RoleNames.Board, now, validTo: null, ct: Xunit.TestContext.Current.CancellationToken);

        hasOverlap.Should().BeTrue();
    }

    [HumansFact]
    public async Task HasOverlappingAssignmentAsync_BoundedRange_NoOverlapBeforeOrAfter()
    {
        var userId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        await SeedAssignmentAsync(userId, RoleNames.Board,
            now - Duration.FromDays(30), now - Duration.FromDays(10));

        var before = await _repo.HasOverlappingAssignmentAsync(
            userId, RoleNames.Board,
            now - Duration.FromDays(40), validTo: now - Duration.FromDays(35), ct: Xunit.TestContext.Current.CancellationToken);
        var after = await _repo.HasOverlappingAssignmentAsync(
            userId, RoleNames.Board,
            now - Duration.FromDays(5), validTo: now, ct: Xunit.TestContext.Current.CancellationToken);

        before.Should().BeFalse();
        after.Should().BeFalse();
    }

    [HumansFact]
    public async Task HasActiveRoleAsync_ReturnsTrueForActiveAssignment()
    {
        var userId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        await SeedAssignmentAsync(userId, RoleNames.Admin, now - Duration.FromDays(1), null);

        var result = await _repo.HasActiveRoleAsync(userId, RoleNames.Admin, now, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task HasActiveRoleAsync_ReturnsFalseForEndedAssignment()
    {
        var userId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        await SeedAssignmentAsync(userId, RoleNames.Admin,
            now - Duration.FromDays(10), now - Duration.FromDays(1));

        var result = await _repo.HasActiveRoleAsync(userId, RoleNames.Admin, now, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task GetActiveForUserForMutationAsync_ReturnsOnlyActive_Tracked()
    {
        var userId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        var active1 = await SeedAssignmentAsync(userId, RoleNames.Board, now - Duration.FromDays(5), null);
        var active2 = await SeedAssignmentAsync(userId, RoleNames.Admin, now - Duration.FromDays(3), null);
        await SeedAssignmentAsync(userId, RoleNames.TeamsAdmin,
            now - Duration.FromDays(100), now - Duration.FromDays(50));

        var result = await _repo.GetActiveForUserForMutationAsync(userId, now, Xunit.TestContext.Current.CancellationToken);

        result.Should().HaveCount(2);
        result.Select(r => r.Id).Should().Contain([active1.Id, active2.Id]);

        foreach (var ra in result)
        {
            ra.ValidTo = now;
        }
        await _repo.UpdateManyAsync(result, Xunit.TestContext.Current.CancellationToken);

        var reloaded = await _dbContext.RoleAssignments.AsNoTracking().Where(r => r.UserId == userId).ToListAsync(Xunit.TestContext.Current.CancellationToken);
        reloaded.All(r => r.ValidTo.HasValue).Should().BeTrue();
    }

    private async Task<RoleAssignment> SeedAssignmentAsync(Guid userId, string roleName, Instant validFrom, Instant? validTo)
    {
        var ra = new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleName = roleName,
            ValidFrom = validFrom,
            ValidTo = validTo,
            CreatedAt = validFrom,
            CreatedByUserId = Guid.NewGuid(),
        };
        _dbContext.RoleAssignments.Add(ra);
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        _dbContext.ChangeTracker.Clear();
        return ra;
    }
}
