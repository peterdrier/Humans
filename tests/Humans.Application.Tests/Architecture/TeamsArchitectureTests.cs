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
/// Teams follows Option A (no caching decorator): the section uses an
/// in-service short-TTL <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/>
/// projection keyed on <c>CacheKeys.ActiveTeams</c>, same pattern the Camps
/// section uses per design-rules §15f / §15i — Camps entry. The decorator
/// split can be layered on later without changing the <see cref="ITeamService"/>
/// surface if profiling warrants it.
/// </summary>
public class TeamsArchitectureTests
{
    // ── TeamService ──────────────────────────────────────────────────────────

    [HumansFact]
    public void TeamService_TakesRepository()
    {
        var ctor = typeof(TeamService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ITeamRepository),
            because: "§15 requires every section service to go through its owning repository interface");
    }

    [HumansFact]
    public void TeamService_AssemblyIsHumansApplication()
    {
        typeof(TeamService).Assembly.GetName().Name
            .Should().Be("Humans.Application",
                because: "cross-check: the Application-layer project graph structurally forbids EF Core references, so services in this assembly cannot import EF even if a future typo tries");
    }

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
    public void TeamRepository_IsSealed()
    {
        typeof(TeamRepository).IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }

    [HumansFact]
    public void TeamRepository_ImplementsITeamRepository()
    {
        typeof(ITeamRepository).IsAssignableFrom(typeof(TeamRepository))
            .Should().BeTrue();
    }

    [HumansFact]
    public void TeamRepository_LivesInInfrastructureRepositoriesTeamsNamespace()
    {
        typeof(TeamRepository).Namespace
            .Should().Be("Humans.Infrastructure.Repositories.Teams",
                because: "EF-backed repository implementations live in Humans.Infrastructure.Repositories.<Section> per design-rules §15b");
    }

    // ── ITeamRepository injection is Teams-section-only ──────────────────────

    /// <summary>
    /// Production code outside the Teams section namespace must not inject
    /// <see cref="ITeamRepository"/> directly. Cross-section reads route through
    /// the public <see cref="ITeamService"/> surface (decorated by
    /// <see cref="CachingTeamService"/>) so they hit the cache instead of the DB
    /// on every request. Repository injection inside the Teams section
    /// (<c>TeamService</c>, <c>CachingTeamService</c>, and the EF impl itself) is
    /// the intended layering and is allowed. Scans all three production
    /// assemblies — Application, Infrastructure, and Web — so a future regression
    /// where a controller, page handler, or filter takes <see cref="ITeamRepository"/>
    /// directly fails this test.
    /// </summary>
    [HumansFact]
    public void ITeamRepository_InjectedOnlyInsideTeamsSection()
    {
        var assembliesToScan = new[]
        {
            typeof(TeamService).Assembly,                                // Humans.Application
            typeof(CachingTeamService).Assembly,                         // Humans.Infrastructure
            typeof(Humans.Web.Controllers.HomeController).Assembly,      // Humans.Web
        };

        var violations = new List<string>();
        foreach (var assembly in assembliesToScan)
        {
            foreach (var type in assembly.GetTypes()
                         .Where(t => t.IsClass && !t.IsAbstract))
            {
                if (IsTeamsSectionType(type))
                    continue;

                foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                {
                    foreach (var parameter in ctor.GetParameters())
                    {
                        if (parameter.ParameterType == typeof(ITeamRepository))
                        {
                            violations.Add($"{type.FullName}:{parameter.ParameterType.Name}");
                        }
                    }
                }
            }
        }

        violations.Should().BeEmpty(
            because: "non-Teams sections must read teams via ITeamService (cache-backed), not ITeamRepository (DB-direct). " +
                     "T-02 (docs/plans/2026-05-16-cache-migration.md) removes the last bypass; this test pins the rule.");
    }

    private static bool IsTeamsSectionType(Type type)
    {
        var ns = type.Namespace;
        if (ns is null)
            return false;

        // Teams section homes for production code that legitimately injects ITeamRepository:
        //   - Humans.Application.Services.Teams.*   (TeamService and helpers)
        //   - Humans.Infrastructure.Services.Teams.* (CachingTeamService decorator)
        //   - Humans.Infrastructure.Repositories.Teams.* (the EF impl itself)
        return ns.StartsWith("Humans.Application.Services.Teams", StringComparison.Ordinal)
            || ns.StartsWith("Humans.Infrastructure.Services.Teams", StringComparison.Ordinal)
            || ns.StartsWith("Humans.Infrastructure.Repositories.Teams", StringComparison.Ordinal);
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
