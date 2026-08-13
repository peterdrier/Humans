using AwesomeAssertions;
using EmailProvisioningService = Humans.GoogleIntegration.Services.EmailProvisioningService;
using GoogleGroupSyncService = Humans.GoogleIntegration.Services.GoogleGroupSyncService;
using GoogleRemovalNotificationService = Humans.GoogleIntegration.Services.GoogleRemovalNotificationService;
using GoogleWorkspaceSyncService = Humans.GoogleIntegration.Services.GoogleWorkspaceSyncService;
using SyncSettingsService = Humans.GoogleIntegration.Services.SyncSettingsService;
using Humans.GoogleIntegration.Services;
using Humans.GoogleIntegration.Services.Workspace;
using Humans.GoogleIntegration.Data;
using Humans.GoogleIntegration.Tests.Infrastructure;
using System.Reflection;
using Microsoft.Extensions.Localization;

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
    public void GoogleGroupSyncService_NamesNoGoogleSdkType() =>
        // Was an assertion about the assembly's references, which held while the service lived
        // in Humans.Application. The section owns the Google.Apis.* packages now, so the
        // statement moved down to the type (G5-SECTION-TEMPLATE.md step 11).
        GoogleSdkContainment.AssertNamesNoGoogleSdkType(typeof(GoogleGroupSyncService));

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

    // ── Resource set (G5-SECTION-TEMPLATE.md step 3b) ────────────────────────

    /// <summary>
    /// The section carved the three <c>GoogleAccounts_*</c> keys out of
    /// <c>SharedResource</c> at its G5 move. <c>Views/_ViewImports.cshtml</c> rebinds
    /// <c>Localizer</c> for every view in one line, so the views are safe by construction —
    /// a controller or service that still takes <c>IStringLocalizer&lt;SharedResource&gt;</c>
    /// keeps compiling and renders those keys as their own names, on a failure path no
    /// render test reaches. Asserted structurally instead.
    /// </summary>
    [HumansFact]
    public void SectionTypesLocalizeThroughTheSectionsOwnResourceSet()
    {
        var offenders = typeof(Section).Assembly.GetTypes()
            .SelectMany(t => t.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(c => c.GetParameters())
                .Concat(t.GetMethods(
                        BindingFlags.Instance | BindingFlags.Static |
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .SelectMany(m => m.GetParameters()))
                .Select(p => (Type: t, p.ParameterType)))
            .Where(x => x.ParameterType.IsGenericType
                        && x.ParameterType.GetGenericTypeDefinition() == typeof(IStringLocalizer<>))
            .Where(x => x.ParameterType.GetGenericArguments()[0] != typeof(GoogleIntegrationResource))
            .Select(x => $"{x.Type.Name} takes {x.ParameterType.Name}")
            .ToList();

        offenders.Should().BeEmpty(
            because: "every localized string the section renders is in GoogleIntegrationResource; binding another set silently degrades those keys to their own names");
    }
}
