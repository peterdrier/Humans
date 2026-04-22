using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;
using TeamResourceService = Humans.Application.Services.Teams.TeamResourceService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository-plus-connector pattern
/// for the Team Resources section — migrated per PR for issue
/// <c>#540c</c> (sub-task of <c>#540</c>).
///
/// <para>
/// Team resource management splits into three clean pieces:
/// <list type="bullet">
///   <item><description>
///     <see cref="ITeamResourceService"/> in <c>Humans.Application.Services.Teams</c>
///     owns business rules + persistence orchestration.
///   </description></item>
///   <item><description>
///     <see cref="IGoogleResourceRepository"/> in <c>Humans.Application.Interfaces.Repositories</c>
///     is the only path to <c>DbSet&lt;GoogleResource&gt;</c>.
///   </description></item>
///   <item><description>
///     <see cref="ITeamResourceGoogleClient"/> is the narrow connector over
///     Drive/Cloud-Identity APIs so the Application project stays free of
///     <c>Google.Apis.*</c> imports.
///   </description></item>
/// </list>
/// These tests pin the invariants so a future refactor can't silently
/// recombine the pieces.
/// </para>
/// </summary>
public class TeamResourceArchitectureTests
{
    // ── TeamResourceService ──────────────────────────────────────────────────

    [Fact]
    public void TeamResourceService_LivesInHumansApplicationServicesTeamsNamespace()
    {
        typeof(TeamResourceService).Namespace
            .Should().Be("Humans.Application.Services.Teams",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [Fact]
    public void TeamResourceService_IsSealed()
    {
        typeof(TeamResourceService).IsSealed
            .Should().BeTrue(
                because: "application services are terminal — extension happens via a caching decorator when warranted (§15d), not subclassing");
    }

    [Fact]
    public void TeamResourceService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(TeamResourceService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use IGoogleResourceRepository instead (design-rules §3)");
    }

    [Fact]
    public void TeamResourceService_TakesRepository()
    {
        var ctor = typeof(TeamResourceService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IGoogleResourceRepository),
            because: "Application services reach persistence through a repository interface (design-rules §3/§15b)");
    }

    [Fact]
    public void TeamResourceService_TakesGoogleConnector()
    {
        var ctor = typeof(TeamResourceService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ITeamResourceGoogleClient),
            because: "Google API calls route through an application-layer connector so Humans.Application stays free of Google.Apis.* imports");
    }

    [Fact]
    public void TeamResourceService_HasNoGoogleApisConstructorParameter()
    {
        var ctor = typeof(TeamResourceService).GetConstructors().Single();
        var googleApiParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Google.Apis.", StringComparison.Ordinal));

        googleApiParam.Should().BeNull(
            because: "the Application layer must not depend on Google.Apis.* — the ITeamResourceGoogleClient connector encapsulates every Google call");
    }

    [Fact]
    public void TeamResourceService_AssemblyIsHumansApplication()
    {
        typeof(TeamResourceService).Assembly.GetName().Name
            .Should().Be("Humans.Application");
    }

    // ── IGoogleResourceRepository ────────────────────────────────────────────

    [Fact]
    public void IGoogleResourceRepository_LivesInApplicationInterfacesRepositoriesNamespace()
    {
        typeof(IGoogleResourceRepository).Namespace
            .Should().Be("Humans.Application.Interfaces.Repositories",
                because: "repository interfaces live in Humans.Application.Interfaces.Repositories per design-rules §3");
    }

    [Fact]
    public void GoogleResourceRepository_IsSealed()
    {
        // Mirrors ProfileRepository — repository implementations are terminal; no subclass should
        // extend or override the EF-backed data access.
        var repoType = typeof(Humans.Infrastructure.Repositories.GoogleResourceRepository);
        repoType.IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }

    [Fact]
    public void GoogleResourceRepository_AssemblyIsHumansInfrastructure()
    {
        typeof(Humans.Infrastructure.Repositories.GoogleResourceRepository).Assembly.GetName().Name
            .Should().Be("Humans.Infrastructure");
    }

    // ── ITeamResourceGoogleClient ────────────────────────────────────────────

    [Fact]
    public void ITeamResourceGoogleClient_LivesInApplicationInterfacesNamespace()
    {
        typeof(ITeamResourceGoogleClient).Namespace
            .Should().Be("Humans.Application.Interfaces");
    }

    [Fact]
    public void ITeamResourceGoogleClient_ExposesNoGoogleApisTypes()
    {
        var methods = typeof(ITeamResourceGoogleClient).GetMethods();

        foreach (var method in methods)
        {
            method.ReturnType.FullName
                .Should().NotStartWith("Google.Apis.",
                    because: $"{method.Name} must not leak Google.Apis.* types into the Application layer");

            foreach (var param in method.GetParameters())
            {
                (param.ParameterType.FullName ?? string.Empty)
                    .Should().NotStartWith("Google.Apis.",
                        because: $"{method.Name}.{param.Name} must not require a Google.Apis.* type");
            }
        }
    }
}
