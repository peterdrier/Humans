using AwesomeAssertions;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Services.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Services.Auth;

/// <summary>
/// Exercises <see cref="CachingRoleAssignmentService"/>'s cache-served paths
/// (<c>GetActiveCountsByRoleAsync</c>, <c>GetActiveForUserAsync</c>) and
/// wholesale invalidation. Pass-through methods are not tested here — the
/// build verifies they satisfy <see cref="IRoleAssignmentService"/>; their
/// behavior is the inner service's behavior, covered by
/// <c>RoleAssignmentServiceTests</c>.
/// </summary>
public class CachingRoleAssignmentServiceTests
{
    [HumansFact]
    public async Task GetActiveCountsByRoleAsync_GroupsActiveRowsByRoleName()
    {
        var now = Instant.FromUtc(2026, 5, 17, 12, 0);
        var clock = new FakeClock(now);
        var repository = Substitute.For<IRoleAssignmentRepository>();
        repository.GetAllRowsForCacheAsync(Arg.Any<CancellationToken>())
            .Returns(new List<RoleAssignment>
            {
                Active("Board", now),
                Active("Board", now),
                Active("Admin", now),
                Expired("Board", now),                   // past — excluded
                Future("Coordinator", now),              // future — excluded
            });

        var service = BuildService(repository, clock);

        var counts = await service.GetActiveCountsByRoleAsync();

        counts.Should().BeEquivalentTo(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Board"] = 2,
            ["Admin"] = 1,
        });
    }

    [HumansFact]
    public async Task GetActiveCountsByRoleAsync_OpenEndedAssignmentsCountAsActive()
    {
        var now = Instant.FromUtc(2026, 5, 17, 12, 0);
        var clock = new FakeClock(now);
        var repository = Substitute.For<IRoleAssignmentRepository>();
        repository.GetAllRowsForCacheAsync(Arg.Any<CancellationToken>())
            .Returns(new List<RoleAssignment>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    RoleName = "Board",
                    ValidFrom = now - Duration.FromDays(30),
                    ValidTo = null,
                },
            });

        var service = BuildService(repository, clock);
        var counts = await service.GetActiveCountsByRoleAsync();

        counts["Board"].Should().Be(1);
    }

    [HumansFact]
    public async Task SecondCall_HitsCache_DoesNotReQueryRepository()
    {
        var now = Instant.FromUtc(2026, 5, 17, 12, 0);
        var clock = new FakeClock(now);
        var repository = Substitute.For<IRoleAssignmentRepository>();
        repository.GetAllRowsForCacheAsync(Arg.Any<CancellationToken>())
            .Returns(new List<RoleAssignment> { Active("Board", now) });

        var service = BuildService(repository, clock);

        await service.GetActiveCountsByRoleAsync();
        await service.GetActiveCountsByRoleAsync();
        await service.GetActiveCountsByRoleAsync();

        await repository.Received(1).GetAllRowsForCacheAsync(Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task InvalidateAll_DropsCache_NextReadReQueriesRepository()
    {
        var now = Instant.FromUtc(2026, 5, 17, 12, 0);
        var clock = new FakeClock(now);
        var repository = Substitute.For<IRoleAssignmentRepository>();
        repository.GetAllRowsForCacheAsync(Arg.Any<CancellationToken>())
            .Returns(new List<RoleAssignment> { Active("Board", now) });

        var service = BuildService(repository, clock);

        await service.GetActiveCountsByRoleAsync();
        service.InvalidateAll();
        await service.GetActiveCountsByRoleAsync();

        await repository.Received(2).GetAllRowsForCacheAsync(Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetActiveCountsByRoleAsync_ReflectsClockAdvance_WithoutInvalidation()
    {
        // The cache holds raw rows; "active" is derived from the clock per
        // call. Advancing the clock past a ValidTo boundary must drop that
        // row's contribution to the count without requiring an explicit
        // invalidation — proves the count is recomputed, not memoized.
        var t0 = Instant.FromUtc(2026, 5, 17, 12, 0);
        var clock = new FakeClock(t0);
        var expiresAt = t0 + Duration.FromHours(1);
        var repository = Substitute.For<IRoleAssignmentRepository>();
        repository.GetAllRowsForCacheAsync(Arg.Any<CancellationToken>())
            .Returns(new List<RoleAssignment>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    RoleName = "Board",
                    ValidFrom = t0 - Duration.FromDays(1),
                    ValidTo = expiresAt,
                },
            });

        var service = BuildService(repository, clock);

        (await service.GetActiveCountsByRoleAsync())["Board"].Should().Be(1);

        clock.Reset(expiresAt + Duration.FromMinutes(1));
        var afterExpiry = await service.GetActiveCountsByRoleAsync();

        afterExpiry.Should().NotContainKey("Board");
    }

    [HumansFact]
    public async Task GetActiveForUserAsync_ReturnsOnlyActiveRolesForUser_OrderedByRoleName()
    {
        var now = Instant.FromUtc(2026, 5, 17, 12, 0);
        var clock = new FakeClock(now);
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var repository = Substitute.For<IRoleAssignmentRepository>();
        repository.GetAllRowsForCacheAsync(Arg.Any<CancellationToken>())
            .Returns(new List<RoleAssignment>
            {
                ActiveFor(userA, "Coordinator", now),
                ActiveFor(userA, "Board", now),
                ExpiredFor(userA, "Admin", now),         // past — excluded
                ActiveFor(userB, "Board", now),          // other user — excluded
            });

        var service = BuildService(repository, clock);

        var roles = await service.GetActiveForUserAsync(userA);

        roles.Select(r => r.RoleName).Should().Equal("Board", "Coordinator");
    }

    [HumansFact]
    public async Task GetActiveForUserAsync_SecondCall_DoesNotReQueryRepository()
    {
        var now = Instant.FromUtc(2026, 5, 17, 12, 0);
        var clock = new FakeClock(now);
        var userId = Guid.NewGuid();
        var repository = Substitute.For<IRoleAssignmentRepository>();
        repository.GetAllRowsForCacheAsync(Arg.Any<CancellationToken>())
            .Returns(new List<RoleAssignment> { ActiveFor(userId, "Board", now) });

        var service = BuildService(repository, clock);

        await service.GetActiveForUserAsync(userId);
        await service.GetActiveForUserAsync(userId);

        await repository.Received(1).GetAllRowsForCacheAsync(Arg.Any<CancellationToken>());
    }

    private static CachingRoleAssignmentService BuildService(
        IRoleAssignmentRepository repository,
        IClock clock)
    {
        var inner = Substitute.For<IRoleAssignmentService>();
        inner.GetFilteredAsync(
                roleFilter: null,
                activeOnly: false,
                page: 1,
                pageSize: int.MaxValue,
                now: Arg.Any<Instant>(),
                ct: Arg.Any<CancellationToken>())
            .Returns(ci => LoadRowsAsync(ci.Arg<CancellationToken>()));

        var services = new ServiceCollection();
        services.AddKeyedScoped<IRoleAssignmentService>(
            CachingRoleAssignmentService.InnerServiceKey, (_, _) => inner);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new CachingRoleAssignmentService(
            scopeFactory,
            clock,
            NullLogger<CachingRoleAssignmentService>.Instance);

        async Task<(IReadOnlyList<RoleAssignmentSummarySnapshot> Items, int TotalCount)> LoadRowsAsync(CancellationToken ct)
        {
            var rows = await repository.GetAllRowsForCacheAsync(ct);
            var items = rows
                .Select(ra => new RoleAssignmentSummarySnapshot(
                    ra.Id,
                    ra.UserId,
                    UserEmail: null,
                    UserDisplayName: string.Empty,
                    ra.RoleName,
                    ra.ValidFrom,
                    ra.ValidTo,
                    Notes: null,
                    CreatedByUserId: Guid.Empty,
                    CreatedByDisplayName: null,
                    CreatedAt: ra.CreatedAt))
                .ToList();
            return (items, items.Count);
        }
    }

    private static RoleAssignment Active(string role, Instant now) =>
        new()
        {
            Id = Guid.NewGuid(),
            RoleName = role,
            ValidFrom = now - Duration.FromDays(30),
            ValidTo = now + Duration.FromDays(30),
        };

    private static RoleAssignment Expired(string role, Instant now) =>
        new()
        {
            Id = Guid.NewGuid(),
            RoleName = role,
            ValidFrom = now - Duration.FromDays(60),
            ValidTo = now - Duration.FromDays(1),
        };

    private static RoleAssignment Future(string role, Instant now) =>
        new()
        {
            Id = Guid.NewGuid(),
            RoleName = role,
            ValidFrom = now + Duration.FromDays(1),
            ValidTo = null,
        };

    private static RoleAssignment ActiveFor(Guid userId, string role, Instant now) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleName = role,
            ValidFrom = now - Duration.FromDays(30),
            ValidTo = now + Duration.FromDays(30),
        };

    private static RoleAssignment ExpiredFor(Guid userId, string role, Instant now) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleName = role,
            ValidFrom = now - Duration.FromDays(60),
            ValidTo = now - Duration.FromDays(1),
        };
}
