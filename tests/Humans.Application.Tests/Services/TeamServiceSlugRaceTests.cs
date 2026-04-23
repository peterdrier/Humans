using AwesomeAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Entities;
using Xunit;
using TeamService = Humans.Application.Services.Teams.TeamService;

namespace Humans.Application.Tests.Services;

/// <summary>
/// Regression tests for Codex PR#300 P2: the create-team slug retry loop
/// no longer catches an EF-specific exception type. Instead,
/// <c>ITeamRepository.AddTeamWithRequiresApprovalOverrideAsync</c> returns
/// <c>bool</c> — <c>false</c> signals a unique-constraint race that the
/// service layer should handle by retrying with the next suffixed slug.
/// These tests mock <c>ITeamRepository</c> so the race can be simulated
/// deterministically without a real Postgres connection (the in-memory
/// provider doesn't enforce unique indexes and wouldn't surface the Npgsql
/// 23505 error code at all).
/// </summary>
public class TeamServiceSlugRaceTests
{
    /// <summary>
    /// Simulated scenario: concurrent create races past the pre-check
    /// (<c>SlugExistsAsync</c>) — first persist attempt loses the unique-key
    /// contest and the repo returns <c>false</c>. The service must retry
    /// with the next suffix rather than bubble an unrecognised exception.
    /// </summary>
    [Fact]
    public async Task CreateTeamAsync_WhenFirstPersistReturnsFalse_RetriesWithSuffixedSlug()
    {
        var repo = BuildRepoForRace(firstAttemptSucceeds: false);
        var service = BuildService(repo);

        var created = await service.CreateTeamAsync(
            name: "Design",
            description: null,
            requiresApproval: true);

        created.Name.Should().Be("Design");
        created.Slug.Should().Be("design-2",
            because: "the first persist returned false (simulated unique-constraint race), so the loop must advance to the suffixed slug");

        // Sanity: two persist attempts, both from the retry loop.
        await repo.Received(2).AddTeamWithRequiresApprovalOverrideAsync(
            Arg.Any<Team>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Happy path: persist succeeds on the first attempt; no retry, no
    /// suffix. Pins the non-race behaviour so the retry logic isn't mistaken
    /// for always-on.
    /// </summary>
    [Fact]
    public async Task CreateTeamAsync_WhenFirstPersistSucceeds_UsesBaseSlug()
    {
        var repo = BuildRepoForRace(firstAttemptSucceeds: true);
        var service = BuildService(repo);

        var created = await service.CreateTeamAsync(
            name: "Design",
            description: null,
            requiresApproval: true);

        created.Slug.Should().Be("design");
        await repo.Received(1).AddTeamWithRequiresApprovalOverrideAsync(
            Arg.Any<Team>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    private static ITeamRepository BuildRepoForRace(bool firstAttemptSucceeds)
    {
        var repo = Substitute.For<ITeamRepository>();

        // Pre-check path — never collides. The race is simulated through the
        // persist step below.
        repo.SlugExistsAsync(Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        // First call: the outcome driven by the test scenario. Second call
        // (if it happens) always succeeds so the loop terminates.
        var callCount = 0;
        repo.AddTeamWithRequiresApprovalOverrideAsync(
                Arg.Any<Team>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1 ? firstAttemptSucceeds : true;
            });

        return repo;
    }

    private static TeamService BuildService(ITeamRepository repo)
    {
        var clock = new FakeClock(Instant.FromUtc(2026, 4, 1, 12, 0));
        var cache = new MemoryCache(new MemoryCacheOptions());

        return new TeamService(
            repo,
            Substitute.For<IAuditLogService>(),
            Substitute.For<INotificationEmitter>(),
            Substitute.For<IShiftManagementService>(),
            Substitute.For<INotificationMeterCacheInvalidator>(),
            Substitute.For<IShiftAuthorizationInvalidator>(),
            Substitute.For<IServiceProvider>(),
            cache,
            clock,
            NullLogger<TeamService>.Instance);
    }
}
