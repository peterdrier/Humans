using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;
using SyncSettingsService = Humans.Application.Services.GoogleIntegration.SyncSettingsService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for the Google
/// Integration section — migration tracked under issue #554.
///
/// <para>
/// First-wave scope: <see cref="SyncSettingsService"/> (<c>sync_service_settings</c>).
/// The remaining Google Integration services (<c>GoogleWorkspaceSyncService</c>,
/// <c>GoogleAdminService</c>, <c>GoogleWorkspaceUserService</c>,
/// <c>DriveActivityMonitorService</c>, <c>EmailProvisioningService</c>) still live
/// in <c>Humans.Infrastructure</c> and inject <c>HumansDbContext</c> directly —
/// see design-rules §15i for the outstanding migration list.
/// </para>
/// </summary>
public class GoogleIntegrationArchitectureTests
{
    // ── SyncSettingsService ──────────────────────────────────────────────────

    [Fact]
    public void SyncSettingsService_LivesInHumansApplicationServicesGoogleIntegrationNamespace()
    {
        typeof(SyncSettingsService).Namespace
            .Should().Be("Humans.Application.Services.GoogleIntegration",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [Fact]
    public void SyncSettingsService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(SyncSettingsService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use ISyncSettingsRepository instead (design-rules §3)");
    }

    [Fact]
    public void SyncSettingsService_HasNoDbContextFactoryConstructorParameter()
    {
        var ctor = typeof(SyncSettingsService).GetConstructors().Single();
        var factoryParam = ctor.GetParameters()
            .FirstOrDefault(p =>
                p.ParameterType.IsGenericType &&
                p.ParameterType.GetGenericTypeDefinition() == typeof(IDbContextFactory<>));

        factoryParam.Should().BeNull(
            because: "IDbContextFactory belongs behind the repository boundary, not in an Application-layer service");
    }

    [Fact]
    public void SyncSettingsService_TakesRepository()
    {
        var ctor = typeof(SyncSettingsService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ISyncSettingsRepository));
    }

    // ── ISyncSettingsRepository ──────────────────────────────────────────────

    [Fact]
    public void ISyncSettingsRepository_LivesInApplicationInterfacesRepositoriesNamespace()
    {
        typeof(ISyncSettingsRepository).Namespace
            .Should().Be("Humans.Application.Interfaces.Repositories",
                because: "repository interfaces live in Humans.Application.Interfaces.Repositories per design-rules §3");
    }

    [Fact]
    public void SyncSettingsRepository_IsSealed()
    {
        var repoType = typeof(Humans.Infrastructure.Repositories.SyncSettingsRepository);

        repoType.IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }
}
