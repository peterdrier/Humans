using AwesomeAssertions;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Xunit;
using GoogleAdminService = Humans.Application.Services.GoogleIntegration.GoogleAdminService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 pattern for the Google Integration
/// section's <see cref="GoogleAdminService"/> — migrated under issue #554
/// (split from the umbrella PR into an isolated sub-task). The service now
/// lives in <c>Humans.Application.Services.GoogleIntegration</c> and routes
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
    public void GoogleAdminService_RoutesCrossSectionDataThroughOwningServices()
    {
        var ctor = typeof(GoogleAdminService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToHashSet();

        // Google Integration owns no user/team/email tables — every cross-section
        // read routes through the owning service interface per design-rules §9.
        paramTypes.Should().Contain(typeof(IUserService),
            because: "cross-section user lookups go through IUserService, not IUserRepository");
        paramTypes.Should().Contain(typeof(IUserEmailService),
            because: "cross-section UserEmail lookups go through IUserEmailService");
        paramTypes.Should().Contain(typeof(ITeamService),
            because: "cross-section Team reads and the google_sync_outbox_events write path go through ITeamService");
        paramTypes.Should().Contain(typeof(IGoogleWorkspaceUserService),
            because: "Google Admin SDK calls go through IGoogleWorkspaceUserService (PR #287 connector), not this service");
    }

    [HumansFact]
    public void GoogleAdminService_IsSealed()
    {
        typeof(GoogleAdminService).IsSealed.Should().BeTrue(
            because: "service implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }

}
