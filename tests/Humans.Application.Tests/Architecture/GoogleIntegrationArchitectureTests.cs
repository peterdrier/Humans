using AwesomeAssertions;
using Humans.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using Xunit;
using EmailProvisioningService = Humans.Application.Services.GoogleIntegration.EmailProvisioningService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for the Google
/// Integration section — migration tracked under issue #554.
///
/// <para>
/// First-wave scope: <see cref="EmailProvisioningService"/>. This PR splits
/// the service out of the umbrella migration (PR #284 shipped
/// <c>SyncSettingsService</c> previously from the same umbrella). The
/// remaining Google Integration services (<c>GoogleWorkspaceSyncService</c>,
/// <c>GoogleAdminService</c>, <c>GoogleWorkspaceUserService</c>,
/// <c>DriveActivityMonitorService</c>) still live in
/// <c>Humans.Infrastructure</c> and inject <c>HumansDbContext</c> directly —
/// see design-rules §15i for the outstanding migration list.
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
}
