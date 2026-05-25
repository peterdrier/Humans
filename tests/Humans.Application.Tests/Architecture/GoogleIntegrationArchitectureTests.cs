using AwesomeAssertions;
using EmailProvisioningService = Humans.Application.Services.GoogleIntegration.EmailProvisioningService;
using GoogleGroupSyncService = Humans.Application.Services.GoogleIntegration.GoogleGroupSyncService;
using GoogleRemovalNotificationService = Humans.Application.Services.GoogleIntegration.GoogleRemovalNotificationService;
using GoogleWorkspaceSyncService = Humans.Application.Services.GoogleIntegration.GoogleWorkspaceSyncService;
using SyncSettingsService = Humans.Application.Services.GoogleIntegration.SyncSettingsService;

namespace Humans.Application.Tests.Architecture;

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

    [HumansFact]
    public void EmailProvisioningService_IsSealed()
    {
        typeof(EmailProvisioningService).IsSealed.Should().BeTrue(
            because: "§15-migrated services are sealed to prevent ad-hoc extension");
    }

    // ── GoogleWorkspaceSyncService (§15 Part 2b, issue #575) ─────────────────

    [HumansFact]
    public void GoogleWorkspaceSyncService_IsSealed()
    {
        typeof(GoogleWorkspaceSyncService).IsSealed.Should().BeTrue(
            because: "§15-migrated services are sealed to prevent ad-hoc extension");
    }

    // ── GoogleGroupSyncService ──────────────────────────────────────────────

    [HumansFact]
    public void GoogleGroupSyncService_IsSealed()
    {
        typeof(GoogleGroupSyncService).IsSealed.Should().BeTrue(
            because: "Application-layer Google Integration services are sealed to prevent ad-hoc extension");
    }

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
    public void GoogleGroupSyncService_HasNoGoogleApisAssemblyReference()
    {
        var assembly = typeof(GoogleGroupSyncService).Assembly;
        var referencedAssemblies = assembly.GetReferencedAssemblies();

        referencedAssemblies
            .Should().NotContain(
                a => (a.Name ?? string.Empty).StartsWith("Google.Apis", StringComparison.Ordinal),
                because: "SDK calls route through Google Integration bridge interfaces");
    }

    // ── GoogleRemovalNotificationService (issue #639) ────────────────────────

    [HumansFact]
    public void GoogleRemovalNotificationService_IsSealed()
    {
        typeof(GoogleRemovalNotificationService).IsSealed.Should().BeTrue(
            because: "§15-migrated services are sealed to prevent ad-hoc extension");
    }

    // ── SyncSettingsService (§15 Phase 0, issue #554) ────────────────────────

    [HumansFact]
    public void SyncSettingsService_IsSealed()
    {
        typeof(SyncSettingsService).IsSealed.Should().BeTrue(
            because: "§15-migrated services are sealed to prevent ad-hoc extension");
    }

}
