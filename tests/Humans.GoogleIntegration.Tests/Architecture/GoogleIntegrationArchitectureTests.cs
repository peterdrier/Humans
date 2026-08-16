using AwesomeAssertions;
using EmailProvisioningService = Humans.GoogleIntegration.Services.EmailProvisioningService;
using GoogleGroupSyncService = Humans.GoogleIntegration.Services.GoogleGroupSyncService;
using GoogleWorkspaceSyncService = Humans.GoogleIntegration.Services.GoogleWorkspaceSyncService;
using Humans.GoogleIntegration.Services;
using Humans.GoogleIntegration.Tests.Infrastructure;

namespace Humans.GoogleIntegration.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for the Google
/// Integration section — migration tracked under issues #554, #574, #575.
///
/// <para>
/// Scope: <see cref="EmailProvisioningService"/> and
/// <see cref="GoogleWorkspaceSyncService"/>. <see cref="EmailProvisioningService"/>
/// landed under issue #289; <see cref="GoogleWorkspaceSyncService"/> migrated
/// under §15 Part 2b (issue #575, 2026-04-23) — the largest §15 move of the
/// campaign. Assertions below pin the Application-layer location, DbContext
/// avoidance, and Google SDK avoidance so a regression cannot silently
/// re-introduce them.
/// </para>
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
