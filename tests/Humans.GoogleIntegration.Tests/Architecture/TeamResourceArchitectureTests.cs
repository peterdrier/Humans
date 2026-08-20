using Humans.GoogleIntegration.Contracts;
using AwesomeAssertions;
using TeamResourceService = Humans.GoogleIntegration.Services.TeamResourceService;
using Humans.GoogleIntegration.Services.Workspace;
using Humans.GoogleIntegration.Data;
using Humans.GoogleIntegration.Tests.Infrastructure;

namespace Humans.GoogleIntegration.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository-plus-connector pattern
/// for the Team Resources section — migrated per PR for issue
/// <c>#540c</c> (sub-task of <c>#540</c>).
///
/// <para>
/// Team resource management splits into three clean pieces:
/// <list type="bullet">
///   <item><description>
///     <see cref="ITeamResourceService"/> in <c>Humans.GoogleIntegration.Services</c>
///     owns business rules + persistence orchestration. The service was
///     relocated from <c>Services.Teams</c> to <c>Services.GoogleIntegration</c>
///     so it sits in the same section as the <see cref="IGoogleResourceRepository"/>
///     it injects (see
///     <c>memory/architecture/team-resources-google-integration-section.md</c>).
///   </description></item>
///   <item><description>
///     <see cref="IGoogleResourceRepository"/> in <c>Humans.Base.Interfaces.Repositories</c>
///     is the only path to <c>DbSet&lt;GoogleResource&gt;</c>.
///   </description></item>
///   <item><description>
///     <see cref="ITeamResourceGoogleClient"/> is the narrow connector over
///     Drive/Cloud-Identity APIs so the Application project stays free of
///     <c>Google.Apis.*</c> imports.
///   </description></item>
/// </list>
/// The tests below cover the first and third pieces — the service takes no
/// Google SDK type, and the connector interface stays in its own namespace and
/// keeps SDK types off its surface. Nothing here checks the second: that the
/// repository is the only path to <c>DbSet&lt;GoogleResource&gt;</c>.
/// </para>
/// </summary>
public class TeamResourceArchitectureTests
{
    // ── TeamResourceService ──────────────────────────────────────────────────

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
    public void ITeamResourceGoogleClient_LivesInTheConnectorNamespace()
    {
        typeof(ITeamResourceGoogleClient).Namespace
            .Should().Be(GoogleSdkContainment.ConnectorNamespace,
                because: "connector interfaces sit with their SDK-touching implementations, which is the section's Google-SDK boundary since the G5 move");
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
