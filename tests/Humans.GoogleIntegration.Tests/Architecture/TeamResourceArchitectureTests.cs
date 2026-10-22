using Humans.GoogleIntegration.Services;
using AwesomeAssertions;
using Humans.GoogleIntegration.Services.Workspace;
using Humans.GoogleIntegration.Data;
using Humans.GoogleIntegration.Tests.Infrastructure;

namespace Humans.GoogleIntegration.Tests.Architecture;

/// <summary>
/// Covers the connector only: <see cref="ITeamResourceGoogleClient"/> stays in its own namespace
/// and keeps SDK types off its surface.
/// </summary>
public class TeamResourceArchitectureTests
{
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
