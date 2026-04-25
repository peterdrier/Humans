using AwesomeAssertions;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Microsoft.EntityFrameworkCore;
using Xunit;
using EmailProvisioningService = Humans.Application.Services.GoogleIntegration.EmailProvisioningService;
using GoogleWorkspaceSyncService = Humans.Application.Services.GoogleIntegration.GoogleWorkspaceSyncService;

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

    [Fact]
    public void EmailProvisioningService_LivesInHumansApplicationServicesGoogleIntegrationNamespace()
    {
        typeof(EmailProvisioningService).Namespace
            .Should().Be("Humans.Application.Services.GoogleIntegration",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [Fact]
    public void EmailProvisioningService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(EmailProvisioningService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — cross-section reads go through service interfaces (design-rules §2b, §9)");
    }

    [Fact]
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

    [Fact]
    public void EmailProvisioningService_HasNoUserManagerConstructorParameter()
    {
        var ctor = typeof(EmailProvisioningService).GetConstructors().Single();
        var userManagerParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.AspNetCore.Identity.UserManager", StringComparison.Ordinal));

        userManagerParam.Should().BeNull(
            because: "User mutations go through IUserService (design-rules §9); UserManager is an Identity-framework concern that belongs to controllers/AccountProvisioningService");
    }

    [Fact]
    public void EmailProvisioningService_DependenciesGoThroughSectionServiceInterfaces()
    {
        var ctor = typeof(EmailProvisioningService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IUserService),
            because: "User reads and GoogleEmail set go through IUserService per design-rules §9");
        paramTypes.Should().Contain(typeof(IProfileService),
            because: "Profile reads (FirstName/LastName) go through IProfileService per design-rules §9");
        paramTypes.Should().Contain(typeof(IUserEmailService),
            because: "UserEmail reads/writes go through IUserEmailService per design-rules §9");
        paramTypes.Should().Contain(typeof(IGoogleWorkspaceUserService),
            because: "Google Workspace Users API calls go through the IGoogleWorkspaceUserService bridge interface (design-rules §13)");
    }

    [Fact]
    public void EmailProvisioningService_HasNoGoogleApisImports()
    {
        // The Google Workspace Users API bridge lives behind IGoogleWorkspaceUserService.
        // The Application-layer service must never reference Google SDK types
        // directly — those belong to the Infrastructure implementation only.
        var assembly = typeof(EmailProvisioningService).Assembly;
        var referencedAssemblies = assembly.GetReferencedAssemblies();

        referencedAssemblies
            .Should().NotContain(
                a => (a.Name ?? string.Empty).StartsWith("Google.Apis", StringComparison.Ordinal),
                because: "Application-layer services must not import Google SDK types; the Google API bridge is IGoogleWorkspaceUserService (design-rules §13)");
    }

    [Fact]
    public void EmailProvisioningService_IsSealed()
    {
        typeof(EmailProvisioningService).IsSealed.Should().BeTrue(
            because: "§15-migrated services are sealed to prevent ad-hoc extension");
    }

    // ── GoogleWorkspaceSyncService (§15 Part 2b, issue #575) ─────────────────

    [Fact]
    public void GoogleWorkspaceSyncService_LivesInHumansApplicationServicesGoogleIntegrationNamespace()
    {
        typeof(GoogleWorkspaceSyncService).Namespace
            .Should().Be("Humans.Application.Services.GoogleIntegration",
                because: "§15 Part 2b (#575) moved the service out of Humans.Infrastructure — see design-rules §15i");
    }

    [Fact]
    public void GoogleWorkspaceSyncService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(GoogleWorkspaceSyncService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — writes go through IGoogleResourceRepository, reads through sibling service interfaces (design-rules §2b, §9)");
    }

    [Fact]
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

    [Fact]
    public void GoogleWorkspaceSyncService_DependenciesGoThroughBridgesAndSectionServiceInterfaces()
    {
        var ctor = typeof(GoogleWorkspaceSyncService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        // Google SDK bridges (Part 2a).
        paramTypes.Should().Contain(typeof(IGoogleGroupMembershipClient));
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

    [Fact]
    public void GoogleWorkspaceSyncService_HasNoGoogleApisAssemblyReference()
    {
        var assembly = typeof(GoogleWorkspaceSyncService).Assembly;
        var referencedAssemblies = assembly.GetReferencedAssemblies();

        referencedAssemblies
            .Should().NotContain(
                a => (a.Name ?? string.Empty).StartsWith("Google.Apis", StringComparison.Ordinal),
                because: "Humans.Application must stay free of Google SDK references — SDK calls route through bridge interfaces (design-rules §13)");
    }

    [Fact]
    public void GoogleWorkspaceSyncService_IsSealed()
    {
        typeof(GoogleWorkspaceSyncService).IsSealed.Should().BeTrue(
            because: "§15-migrated services are sealed to prevent ad-hoc extension");
    }
}
