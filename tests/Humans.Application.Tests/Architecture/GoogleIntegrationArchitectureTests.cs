using AwesomeAssertions;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Microsoft.EntityFrameworkCore;
using Xunit;
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
    public void EmailProvisioningService_LivesInHumansApplicationServicesGoogleIntegrationNamespace()
    {
        typeof(EmailProvisioningService).Namespace
            .Should().Be("Humans.Application.Services.GoogleIntegration",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [HumansFact]
    public void EmailProvisioningService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(EmailProvisioningService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — cross-section reads go through service interfaces (design-rules §2b, §9)");
    }

    [HumansFact]
    public void EmailProvisioningService_HasNoDbContextFactoryConstructorParameter()
    {
        var ctor = typeof(EmailProvisioningService).GetConstructors().Single();
        var factoryParam = ctor.GetParameters()
            .FirstOrDefault(p =>
                p.ParameterType.IsGenericType &&
                p.ParameterType.GetGenericTypeDefinition() == typeof(IDbContextFactory<>));

        factoryParam.Should().BeNull(
            because: "IDbContextFactory belongs behind a repository or another service boundary, not in an Application-layer service");
    }

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
    public void EmailProvisioningService_DependenciesGoThroughSectionServiceInterfaces()
    {
        var ctor = typeof(EmailProvisioningService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IUserService),
            because: "User and Profile reads (including FirstName/LastName) go through the cached UserInfo read-model on IUserService.GetUserInfoAsync per design-rules §9; GoogleEmail set also routes through IUserService");
        paramTypes.Should().NotContain(typeof(IProfileService),
            because: "Profile reads moved off IProfileService onto IUserService.GetUserInfoAsync — IProfileService is being retired in the IUserService consolidation");
        paramTypes.Should().Contain(typeof(IUserEmailService),
            because: "UserEmail reads/writes go through IUserEmailService per design-rules §9");
        paramTypes.Should().Contain(typeof(IGoogleWorkspaceUserService),
            because: "Google Workspace Users API calls go through the IGoogleWorkspaceUserService bridge interface (design-rules §13)");
    }

    [HumansFact]
    public void EmailProvisioningService_IsSealed()
    {
        typeof(EmailProvisioningService).IsSealed.Should().BeTrue(
            because: "§15-migrated services are sealed to prevent ad-hoc extension");
    }

    // ── GoogleWorkspaceSyncService (§15 Part 2b, issue #575) ─────────────────

    [HumansFact]
    public void GoogleWorkspaceSyncService_LivesInHumansApplicationServicesGoogleIntegrationNamespace()
    {
        typeof(GoogleWorkspaceSyncService).Namespace
            .Should().Be("Humans.Application.Services.GoogleIntegration",
                because: "§15 Part 2b (#575) moved the service out of Humans.Infrastructure — see design-rules §15i");
    }

    [HumansFact]
    public void GoogleWorkspaceSyncService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(GoogleWorkspaceSyncService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — writes go through IGoogleResourceRepository, reads through sibling service interfaces (design-rules §2b, §9)");
    }

    [HumansFact]
    public void GoogleWorkspaceSyncService_HasNoDbContextFactoryConstructorParameter()
    {
        var ctor = typeof(GoogleWorkspaceSyncService).GetConstructors().Single();
        var factoryParam = ctor.GetParameters()
            .FirstOrDefault(p =>
                p.ParameterType.IsGenericType &&
                p.ParameterType.GetGenericTypeDefinition() == typeof(IDbContextFactory<>));

        factoryParam.Should().BeNull(
            because: "IDbContextFactory belongs behind a repository or another service boundary — it was retired in §15 Part 2b");
    }

    [HumansFact]
    public void GoogleWorkspaceSyncService_DependenciesGoThroughBridgesAndSectionServiceInterfaces()
    {
        var ctor = typeof(GoogleWorkspaceSyncService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        // Google SDK bridges (Part 2a). Group membership calls live in
        // GoogleGroupSyncService; this service only provisions groups and
        // manages settings drift.
        paramTypes.Should().NotContain(typeof(IGoogleGroupMembershipClient));
        paramTypes.Should().Contain(typeof(IGoogleGroupSync),
            because: "group membership sync requests after Drive resource link/unlink route through IGoogleGroupSync.RequestSyncAsync");
        paramTypes.Should().Contain(typeof(IGoogleGroupProvisioningClient));
        paramTypes.Should().Contain(typeof(IGoogleDrivePermissionsClient));
        paramTypes.Should().Contain(typeof(IGoogleDirectoryClient));

        // Sibling-service cross-section reads.
        paramTypes.Should().Contain(typeof(ITeamService),
            because: "team/member reads route through ITeamService per design-rules §9");
        paramTypes.Should().Contain(typeof(IUserService),
            because: "User reads route through IUserService");
        paramTypes.Should().Contain(typeof(IUserEmailService),
            because: "extra-email identity resolution routes through IUserEmailService");

        // Repositories for section-owned tables.
        paramTypes.Should().Contain(typeof(IGoogleResourceRepository));
        paramTypes.Should().Contain(typeof(IGoogleSyncOutboxRepository));
    }

    [HumansFact]
    public void GoogleWorkspaceSyncService_IsSealed()
    {
        typeof(GoogleWorkspaceSyncService).IsSealed.Should().BeTrue(
            because: "§15-migrated services are sealed to prevent ad-hoc extension");
    }

    // ── GoogleGroupSyncService ──────────────────────────────────────────────

    [HumansFact]
    public void GoogleGroupSyncService_LivesInHumansApplicationServicesGoogleIntegrationNamespace()
    {
        typeof(GoogleGroupSyncService).Namespace
            .Should().Be("Humans.Application.Services.GoogleIntegration",
                because: "Google group membership orchestration is Application-layer Google Integration business logic");
    }

    [HumansFact]
    public void GoogleGroupSyncService_IsSealed()
    {
        typeof(GoogleGroupSyncService).IsSealed.Should().BeTrue(
            because: "Application-layer Google Integration services are sealed to prevent ad-hoc extension");
    }

    [HumansFact]
    public void GoogleGroupSyncService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(GoogleGroupSyncService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext");
    }

    [HumansFact]
    public void GoogleGroupSyncService_HasNoDbContextFactoryConstructorParameter()
    {
        var ctor = typeof(GoogleGroupSyncService).GetConstructors().Single();
        var factoryParam = ctor.GetParameters()
            .FirstOrDefault(p =>
                p.ParameterType.IsGenericType &&
                p.ParameterType.GetGenericTypeDefinition() == typeof(IDbContextFactory<>));

        factoryParam.Should().BeNull(
            because: "IDbContextFactory belongs behind a repository or another service boundary, not in an Application-layer service");
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
    public void GoogleRemovalNotificationService_LivesInHumansApplicationServicesGoogleIntegrationNamespace()
    {
        typeof(GoogleRemovalNotificationService).Namespace
            .Should().Be("Humans.Application.Services.GoogleIntegration",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [HumansFact]
    public void GoogleRemovalNotificationService_IsSealed()
    {
        typeof(GoogleRemovalNotificationService).IsSealed.Should().BeTrue(
            because: "§15-migrated services are sealed to prevent ad-hoc extension");
    }

    [HumansFact]
    public void GoogleRemovalNotificationService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(GoogleRemovalNotificationService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — cross-section reads go through service interfaces (design-rules §2b, §9)");
    }

    [HumansFact]
    public void GoogleRemovalNotificationService_HasNoDbContextFactoryConstructorParameter()
    {
        var ctor = typeof(GoogleRemovalNotificationService).GetConstructors().Single();
        var factoryParam = ctor.GetParameters()
            .FirstOrDefault(p =>
                p.ParameterType.IsGenericType &&
                p.ParameterType.GetGenericTypeDefinition() == typeof(IDbContextFactory<>));

        factoryParam.Should().BeNull(
            because: "IDbContextFactory belongs behind a repository or another service boundary, not in an Application-layer service");
    }

    // ── SyncSettingsService (§15 Phase 0, issue #554) ────────────────────────

    [HumansFact]
    public void SyncSettingsService_LivesInHumansApplicationServicesGoogleIntegrationNamespace()
    {
        typeof(SyncSettingsService).Namespace
            .Should().Be("Humans.Application.Services.GoogleIntegration",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [HumansFact]
    public void SyncSettingsService_IsSealed()
    {
        typeof(SyncSettingsService).IsSealed.Should().BeTrue(
            because: "§15-migrated services are sealed to prevent ad-hoc extension");
    }

    [HumansFact]
    public void SyncSettingsService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(SyncSettingsService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — writes go through ISyncSettingsRepository (design-rules §2b, §9)");
    }

    [HumansFact]
    public void SyncSettingsService_HasNoDbContextFactoryConstructorParameter()
    {
        var ctor = typeof(SyncSettingsService).GetConstructors().Single();
        var factoryParam = ctor.GetParameters()
            .FirstOrDefault(p =>
                p.ParameterType.IsGenericType &&
                p.ParameterType.GetGenericTypeDefinition() == typeof(IDbContextFactory<>));

        factoryParam.Should().BeNull(
            because: "IDbContextFactory belongs behind a repository or another service boundary, not in an Application-layer service");
    }
}
