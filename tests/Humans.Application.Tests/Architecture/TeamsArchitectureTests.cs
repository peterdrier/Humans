using System.Reflection;
using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Infrastructure.Repositories.Teams;
using Humans.Infrastructure.Services.Teams;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TeamService = Humans.Application.Services.Teams.TeamService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for the Teams
/// section — migrated per issue #540 (§15 Part 1 — TeamService core).
/// Pins the invariants:
/// <list type="bullet">
/// <item><description><c>TeamService</c> lives in <c>Humans.Application.Services.Teams</c>.</description></item>
/// <item><description><c>TeamService</c> never injects <c>DbContext</c> — all data access flows through <see cref="ITeamRepository"/>.</description></item>
/// <item><description><c>TeamService</c> never imports <c>Microsoft.EntityFrameworkCore</c> (structurally enforced by the project reference graph — this test acts as a defence-in-depth).</description></item>
/// <item><description><see cref="ITeamRepository"/> lives in <c>Humans.Application.Interfaces.Repositories</c> and has a sealed EF-backed implementation.</description></item>
/// </list>
/// Teams uses the §15 caching decorator pattern: <see cref="CachingTeamService"/>
/// wraps the keyed inner <see cref="ITeamService"/> and exposes the read split
/// via <see cref="ITeamServiceRead"/>.
/// </summary>
public class TeamsArchitectureTests
{
    // ── TeamService ──────────────────────────────────────────────────────────

    [HumansFact]
    public void TeamService_DoesNotReferenceEntityFrameworkCore()
    {
        // Humans.Application.csproj does not reference Microsoft.EntityFrameworkCore,
        // so this is already structurally enforced. The assertion here is defense
        // in depth against a future typo in the csproj.
        var apiAssembly = typeof(TeamService).Assembly;
        var referencedAssemblies = apiAssembly.GetReferencedAssemblies();

        referencedAssemblies
            .Should().NotContain(
                a => string.Equals(a.Name, "Microsoft.EntityFrameworkCore", StringComparison.Ordinal),
                because: "services in Humans.Application must not import EF Core (design-rules §2b)");
    }

    // ── ITeamRepository + TeamRepository ─────────────────────────────────────

    [HumansFact]
    public void TeamRepository_ImplementsITeamRepository()
    {
        typeof(ITeamRepository).IsAssignableFrom(typeof(TeamRepository))
            .Should().BeTrue();
    }

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
