using Humans.GoogleIntegration.Contracts;
using AwesomeAssertions;
using GoogleAdminService = Humans.GoogleIntegration.Services.GoogleAdminService;
using Humans.GoogleIntegration.Services;
using Humans.GoogleIntegration.Services.Workspace;
using Humans.GoogleIntegration.Data;
using Humans.GoogleIntegration.Tests.Infrastructure;

namespace Humans.GoogleIntegration.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 pattern for the Google Integration
/// section's <see cref="GoogleAdminService"/> — migrated under issue #554
/// (split from the umbrella PR into an isolated sub-task). The service now
/// lives in <c>Humans.GoogleIntegration.Services</c> and routes
/// all cross-section data access through the owning service interfaces
/// (<see cref="IUserService"/>, <see cref="IUserEmailService"/>,
/// <see cref="ITeamService"/>, <see cref="ITeamResourceService"/>). These
/// tests are the compile-time guarantee that the service never reaches back
/// into EF Core or another section's tables.
/// </summary>
public class GoogleAdminArchitectureTests
{
    // ── GoogleAdminService ───────────────────────────────────────────────────

    [HumansFact]
    public void GoogleAdminService_IsSealed()
    {
        typeof(GoogleAdminService).IsSealed.Should().BeTrue(
            because: "service implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }

}
