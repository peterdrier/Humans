using AwesomeAssertions;
using Humans.Application.Interfaces.GoogleIntegration;
using TeamResourceService = Humans.Application.Services.GoogleIntegration.TeamResourceService;

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
///     <see cref="ITeamResourceService"/> in <c>Humans.Application.Services.GoogleIntegration</c>
///     owns business rules + persistence orchestration. The service was
///     relocated from <c>Services.Teams</c> to <c>Services.GoogleIntegration</c>
///     so HUM0017 sees its <see cref="IGoogleResourceRepository"/> injection
///     as intra-section (see
///     <c>memory/architecture/team-resources-google-integration-section.md</c>).
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

    [HumansFact]
    public void TeamResourceService_IsSealed()
    {
        typeof(TeamResourceService).IsSealed
            .Should().BeTrue(
                because: "application services are terminal — extension happens via a caching decorator when warranted (§15d), not subclassing");
    }

    [HumansFact]
    public void TeamResourceService_HasNoGoogleApisConstructorParameter()
    {
        var ctor = typeof(TeamResourceService).GetConstructors().Single();
        var googleApiParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Google.Apis.", StringComparison.Ordinal));

        googleApiParam.Should().BeNull(
            because: "the Application layer must not depend on Google.Apis.* — the ITeamResourceGoogleClient connector encapsulates every Google call");
    }

    // ── ITeamResourceGoogleClient ────────────────────────────────────────────

    [HumansFact]
    public void ITeamResourceGoogleClient_LivesInApplicationInterfacesNamespace()
    {
        typeof(ITeamResourceGoogleClient).Namespace
            .Should().Be("Humans.Application.Interfaces.GoogleIntegration");
    }

    [HumansFact]
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
