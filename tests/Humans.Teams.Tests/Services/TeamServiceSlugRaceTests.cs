using AwesomeAssertions;
using Humans.Base.Interfaces.Caching;
using Humans.AuditLog.Contracts;
using Humans.Auth.Contracts;
using Humans.EarlyEntry.Contracts;
using Humans.GoogleIntegration.Contracts;
using Humans.Notifications.Contracts;
using Humans.Shifts.Contracts;
using Humans.Shifts.Services;
using Humans.Teams.Data;
using Humans.Teams.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using TeamService = Humans.Teams.Services.TeamService;

namespace Humans.Teams.Tests.Services;

/// <summary>
/// The repository is mocked so the unique-key race is deterministic: InMemory enforces no unique
/// indexes. A lost race returns false, and the service retries with the next suffixed slug.
/// </summary>
public class TeamServiceSlugRaceTests
{
    /// <summary>
    /// A concurrent create races past <c>SlugExistsAsync</c> and the first persist loses: the service
    /// must retry with the next suffix rather than bubble an exception.
    /// </summary>
    [HumansFact]
    public async Task CreateTeamAsync_WhenFirstPersistReturnsFalse_RetriesWithSuffixedSlug()
    {
        var repo = BuildRepoForRace(firstAttemptSucceeds: false);
        var service = BuildService(repo);

        var created = await service.CreateTeamAsync(
            name: "Design",
            description: null,
            requiresApproval: true, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        created.Name.Should().Be("Design");
        created.Slug.Should().Be("design-2",
            because: "the first persist returned false (simulated unique-constraint race), so the loop must advance to the suffixed slug");

        // Sanity: two persist attempts, both from the retry loop.
        await repo.Received(2).AddTeamWithRequiresApprovalOverrideAsync(
            Arg.Any<Team>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    /// <summary>No race: no retry, no suffix, so the retry logic is not mistaken for always-on.</summary>
    [HumansFact]
    public async Task CreateTeamAsync_WhenFirstPersistSucceeds_UsesBaseSlug()
    {
        var repo = BuildRepoForRace(firstAttemptSucceeds: true);
        var service = BuildService(repo);

        var created = await service.CreateTeamAsync(
            name: "Design",
            description: null,
            requiresApproval: true, cancellationToken: Xunit.TestContext.Current.CancellationToken);

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

        return new TeamService(
            repo,
            Substitute.For<IAuditLogService>(),
            Substitute.For<INotificationEmitter>(),
            Substitute.For<IShiftManagement>(),
            Substitute.For<IAdminAuthorizationService>(),
            new ServiceLocatorBuilder()
                .With<IGoogleSyncOutboxService>()
                .With<IGoogleIntegrationService>()
                .Build(),
            clock,
            NullLogger<TeamService>.Instance);
    }
}
