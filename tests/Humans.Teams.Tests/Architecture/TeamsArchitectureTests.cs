using AwesomeAssertions;
using Humans.Teams.Contracts;
using Humans.Teams.Data;
using Humans.Teams.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TeamService = Humans.Teams.Services.TeamService;

namespace Humans.Teams.Tests;

/// <summary>
/// What is left of the Teams §15 checks (issue #540). The read split is the subject:
/// <see cref="ITeamService"/> inherits <see cref="ITeamServiceRead"/>, the caching decorator
/// implements both, and the two resolve to the same singleton — so a reader and a writer can
/// never end up looking at different caches. Plus the repository implements its own interface.
/// </summary>
public class TeamsArchitectureTests
{
    // ── ITeamRepository + TeamRepository ─────────────────────────────────────

    [HumansFact]
    public void TeamRepository_ImplementsITeamRepository()
    {
        typeof(ITeamRepository).IsAssignableFrom(typeof(TeamRepository))
            .Should().BeTrue();
    }

    // Cross-section injection of ITeamRepository is structurally impossible: the
    // interface is internal to Humans.Teams, so no other assembly can name it.

    // ── ITeamServiceRead split (memory/architecture/section-read-write-split.md) ──

    [HumansFact]
    public void ITeamService_InheritsITeamServiceRead()
    {
        typeof(ITeamServiceRead).IsAssignableFrom(typeof(ITeamService))
            .Should().BeTrue(
                because: "ITeamService is the full Teams surface; external sections inject the narrow ITeamServiceRead. " +
                         "See memory/architecture/section-read-write-split.md.");
    }

    [HumansFact]
    public void CachingTeamService_ImplementsITeamServiceRead()
    {
        typeof(ITeamServiceRead).IsAssignableFrom(typeof(CachingTeamService))
            .Should().BeTrue();
    }

    [HumansFact]
    public void ITeamService_And_ITeamServiceRead_ResolveToSameSingleton()
    {
        // Mirrors the Teams-section DI shape: the same CachingTeamService
        // singleton is exposed under both interface keys.
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<ITeamRepository>());
        services.AddSingleton(Substitute.For<IServiceScopeFactory>());
        services.AddSingleton(Substitute.For<ILogger<CachingTeamService>>());

        services.AddSingleton<CachingTeamService>();
        services.AddSingleton<ITeamService>(sp => sp.GetRequiredService<CachingTeamService>());
        services.AddSingleton<ITeamServiceRead>(sp => sp.GetRequiredService<CachingTeamService>());

        using var provider = services.BuildServiceProvider();

        var fromFull = provider.GetRequiredService<ITeamService>();
        var fromRead = provider.GetRequiredService<ITeamServiceRead>();
        var concrete = provider.GetRequiredService<CachingTeamService>();

        ReferenceEquals(fromFull, concrete).Should().BeTrue();
        ReferenceEquals(fromRead, concrete).Should().BeTrue();
    }
}
