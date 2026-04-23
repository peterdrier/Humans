using System.Reflection;
using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Infrastructure.Repositories.Teams;
using Microsoft.EntityFrameworkCore;
using Xunit;
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

    [Fact]
    public void TeamService_LivesInHumansApplicationServicesTeamsNamespace()
    {
        typeof(TeamService).Namespace
            .Should().Be("Humans.Application.Services.Teams",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [Fact]
    public void TeamService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(TeamService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use ITeamRepository instead (design-rules §3)");
    }

    [Fact]
    public void TeamService_HasNoIDbContextFactoryConstructorParameter()
    {
        var ctor = typeof(TeamService).GetConstructors().Single();
        var factoryParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.EntityFrameworkCore.IDbContextFactory", StringComparison.Ordinal));

        factoryParam.Should().BeNull(
            because: "IDbContextFactory is a repository concern (design-rules §15b) — services go through repositories, not straight to the factory");
    }

    [Fact]
    public void TeamService_TakesRepository()
    {
        var ctor = typeof(TeamService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ITeamRepository),
            because: "§15 requires every section service to go through its owning repository interface");
    }

    [Fact]
    public void TeamService_AssemblyIsHumansApplication()
    {
        typeof(TeamService).Assembly.GetName().Name
            .Should().Be("Humans.Application",
                because: "cross-check: the Application-layer project graph structurally forbids EF Core references, so services in this assembly cannot import EF even if a future typo tries");
    }

    [Fact]
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

    [Fact]
    public void ITeamRepository_LivesInApplicationInterfacesRepositoriesNamespace()
    {
        typeof(ITeamRepository).Namespace
            .Should().Be("Humans.Application.Interfaces.Repositories",
                because: "repository interfaces live in Humans.Application.Interfaces.Repositories per design-rules §3");
    }

    [Fact]
    public void TeamRepository_IsSealed()
    {
        typeof(TeamRepository).IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }

    [Fact]
    public void TeamRepository_ImplementsITeamRepository()
    {
        typeof(ITeamRepository).IsAssignableFrom(typeof(TeamRepository))
            .Should().BeTrue();
    }

    [Fact]
    public void TeamRepository_LivesInInfrastructureRepositoriesTeamsNamespace()
    {
        typeof(TeamRepository).Namespace
            .Should().Be("Humans.Infrastructure.Repositories.Teams",
                because: "EF-backed repository implementations live in Humans.Infrastructure.Repositories.<Section> per design-rules §15b");
    }
}
