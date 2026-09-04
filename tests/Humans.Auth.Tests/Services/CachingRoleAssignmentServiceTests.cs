using AwesomeAssertions;
using Humans.Auth.Contracts;
using Humans.Auth.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Auth.Tests.Services;

/// <summary>
/// Exercises <see cref="CachingRoleAssignmentService"/>'s cache-served paths
/// (<c>GetActiveCountsByRoleAsync</c>, <c>GetActiveForUserAsync</c>) and
/// wholesale invalidation. Pass-through methods are not tested here — the
/// build verifies they satisfy <see cref="IRoleAssignmentService"/>; their
/// behavior is the inner service's behavior, covered by
/// <c>RoleAssignmentServiceTests</c>.
/// </summary>
/// <remarks>
/// The decorator's only collaborator is the keyed inner
/// <see cref="IRoleAssignmentService"/>, resolved per call through
/// <c>IServiceScopeFactory</c> — it never sees a repository. So the warm-count
/// assertions below are made against the inner service itself, and
/// <c>GetFilteredAsync</c> is stubbed with <c>Arg.Any</c>: matching the warm
/// call's exact arguments would turn a deliberate change to the warm path into
/// a null-tuple crash instead of a readable failure. The one place the warm
/// call's shape is asserted is <see cref="WarmsByAskingInnerForEveryRow"/>.
/// </remarks>
public sealed class CachingRoleAssignmentServiceTests
{
    [HumansFact]
    public async Task GetActiveCountsByRoleAsync_GroupsActiveRowsByRoleName()
    {
        var now = Instant.FromUtc(2026, 5, 17, 12, 0);
        var (service, _) = BuildService(
            [
                Active("Board", now),
                Active("Board", now),
                Active("Admin", now),
                Expired("Board", now),                   // past — excluded
                Future("Coordinator", now),              // future — excluded
            ],
            new FakeClock(now));

        var counts = await service.GetActiveCountsByRoleAsync(Xunit.TestContext.Current.CancellationToken);

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
        var (service, _) = BuildService(
            [Row(Guid.NewGuid(), "Board", now - Duration.FromDays(30), validTo: null)],
            new FakeClock(now));

        var counts = await service.GetActiveCountsByRoleAsync(Xunit.TestContext.Current.CancellationToken);

        counts["Board"].Should().Be(1);
    }

    [HumansFact]
    public async Task WarmsByAskingInnerForEveryRow()
    {
        var now = Instant.FromUtc(2026, 5, 17, 12, 0);
        var (service, inner) = BuildService([Active("Board", now)], new FakeClock(now));

        await service.GetActiveCountsByRoleAsync(Xunit.TestContext.Current.CancellationToken);

        // The cache derives "active at now" per call, so the warm must fetch every
        // row — unfiltered and unpaged — not just the ones active when it ran.
        await inner.Received(1).GetFilteredAsync(
            roleFilter: null,
            activeOnly: false,
            page: 1,
            pageSize: int.MaxValue,
            now: Arg.Any<Instant>(),
            ct: Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SecondCall_HitsCache_DoesNotReQueryInner()
    {
        var now = Instant.FromUtc(2026, 5, 17, 12, 0);
        var userId = Guid.NewGuid();
        var (service, inner) = BuildService([ActiveFor(userId, "Board", now)], new FakeClock(now));

        // Warming is per-cache, not per-method: a second read through a *different*
        // cache-served method must not re-warm either.
        await service.GetActiveCountsByRoleAsync(Xunit.TestContext.Current.CancellationToken);
        await service.GetActiveCountsByRoleAsync(Xunit.TestContext.Current.CancellationToken);
        await service.GetActiveForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        await inner.Received(1).GetFilteredAsync(
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task InvalidateAll_DropsCache_NextReadReWarms()
    {
        var now = Instant.FromUtc(2026, 5, 17, 12, 0);
        var (service, inner) = BuildService([Active("Board", now)], new FakeClock(now));

        await service.GetActiveCountsByRoleAsync(Xunit.TestContext.Current.CancellationToken);
        service.InvalidateAll();
        await service.GetActiveCountsByRoleAsync(Xunit.TestContext.Current.CancellationToken);

        await inner.Received(2).GetFilteredAsync(
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<Instant>(), Arg.Any<CancellationToken>());
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
        var (service, _) = BuildService(
            [Row(Guid.NewGuid(), "Board", t0 - Duration.FromDays(1), expiresAt)],
            clock);

        (await service.GetActiveCountsByRoleAsync(Xunit.TestContext.Current.CancellationToken))["Board"].Should().Be(1);

        clock.Reset(expiresAt + Duration.FromMinutes(1));
        var afterExpiry = await service.GetActiveCountsByRoleAsync(Xunit.TestContext.Current.CancellationToken);

        afterExpiry.Should().NotContainKey("Board");
    }

    [HumansFact]
    public async Task GetActiveForUserAsync_ReturnsOnlyActiveRolesForUser_OrderedByRoleName()
    {
        var now = Instant.FromUtc(2026, 5, 17, 12, 0);
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var (service, _) = BuildService(
            [
                ActiveFor(userA, "Coordinator", now),
                ActiveFor(userA, "Board", now),
                ExpiredFor(userA, "Admin", now),         // past — excluded
                ActiveFor(userB, "Board", now),          // other user — excluded
            ],
            new FakeClock(now));

        var roles = await service.GetActiveForUserAsync(userA, Xunit.TestContext.Current.CancellationToken);

        roles.Select(r => r.RoleName).Should().Equal("Board", "Coordinator");
    }

    private static (CachingRoleAssignmentService Service, IRoleAssignmentService Inner) BuildService(
        IReadOnlyList<RoleAssignmentSummarySnapshot> rows,
        IClock clock)
    {
        var inner = Substitute.For<IRoleAssignmentService>();
        inner.GetFilteredAsync(
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult((rows, rows.Count)));

        var services = new ServiceCollection();
        services.AddKeyedScoped<IRoleAssignmentService>(
            CachingRoleAssignmentService.InnerServiceKey, (_, _) => inner);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var service = new CachingRoleAssignmentService(
            scopeFactory,
            clock,
            NullLogger<CachingRoleAssignmentService>.Instance);

        return (service, inner);
    }

    private static RoleAssignmentSummarySnapshot Row(
        Guid userId, string role, Instant validFrom, Instant? validTo) =>
        new(
            Id: Guid.NewGuid(),
            UserId: userId,
            UserEmail: null,
            UserDisplayName: string.Empty,
            RoleName: role,
            ValidFrom: validFrom,
            ValidTo: validTo,
            Notes: null,
            CreatedByUserId: Guid.Empty,
            CreatedByDisplayName: null,
            CreatedAt: validFrom);

    private static RoleAssignmentSummarySnapshot Active(string role, Instant now) =>
        ActiveFor(Guid.NewGuid(), role, now);

    private static RoleAssignmentSummarySnapshot Expired(string role, Instant now) =>
        ExpiredFor(Guid.NewGuid(), role, now);

    private static RoleAssignmentSummarySnapshot Future(string role, Instant now) =>
        Row(Guid.NewGuid(), role, now + Duration.FromDays(1), validTo: null);

    private static RoleAssignmentSummarySnapshot ActiveFor(Guid userId, string role, Instant now) =>
        Row(userId, role, now - Duration.FromDays(30), now + Duration.FromDays(30));

    private static RoleAssignmentSummarySnapshot ExpiredFor(Guid userId, string role, Instant now) =>
        Row(userId, role, now - Duration.FromDays(60), now - Duration.FromDays(1));
}
