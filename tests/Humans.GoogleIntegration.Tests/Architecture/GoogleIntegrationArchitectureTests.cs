using AwesomeAssertions;
using EmailProvisioningService = Humans.GoogleIntegration.Services.EmailProvisioningService;
using GoogleGroupSyncService = Humans.GoogleIntegration.Services.GoogleGroupSyncService;
using Humans.GoogleIntegration.Tests.Infrastructure;

namespace Humans.GoogleIntegration.Tests.Architecture;

/// <summary>
/// Three checks on two of the section's services. Neither may take Identity's
/// <c>UserManager</c> — changing a user is the user section's job, through its own service —
/// and the group sync may not name a Google SDK type, which is what keeps the SDK behind the
/// connector interfaces.
/// </summary>
public class GoogleIntegrationArchitectureTests
{
    // ── EmailProvisioningService ─────────────────────────────────────────────

    [HumansFact]
    public void EmailProvisioningService_HasNoUserManagerConstructorParameter()
    {
        var ctor = typeof(EmailProvisioningService).GetConstructors().Single();
        var userManagerParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.AspNetCore.Identity.UserManager", StringComparison.Ordinal));

        userManagerParam.Should().BeNull(
            because: "User mutations go through IUserService (design-rules §9); UserManager is an Identity-framework concern that belongs to controllers/AccountProvisioningService");
    }

    // ── GoogleGroupSyncService ──────────────────────────────────────────────

    [HumansFact]
    public void GoogleGroupSyncService_HasNoUserManagerConstructorParameter()
    {
        var ctor = typeof(GoogleGroupSyncService).GetConstructors().Single();
        var userManagerParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.AspNetCore.Identity.UserManager", StringComparison.Ordinal));

        userManagerParam.Should().BeNull(
            because: "User mutations go through user-section service interfaces");
    }

    [HumansFact]
    public void GoogleGroupSyncService_NamesNoGoogleSdkType() =>
        // Was an assertion about the assembly's references, which held while the service lived
        // in Humans.Application. The section owns the Google.Apis.* packages now, so the
        // statement moved down to the type (G5-SECTION-TEMPLATE.md step 11).
        GoogleSdkContainment.AssertNamesNoGoogleSdkType(typeof(GoogleGroupSyncService));
}
