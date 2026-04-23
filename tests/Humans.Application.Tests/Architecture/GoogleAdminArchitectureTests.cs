using AwesomeAssertions;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Microsoft.EntityFrameworkCore;
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

    [Fact]
    public void GoogleAdminService_LivesInHumansApplicationServicesGoogleIntegrationNamespace()
    {
        typeof(GoogleAdminService).Namespace
            .Should().Be("Humans.Application.Services.GoogleIntegration",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [Fact]
    public void GoogleAdminService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(GoogleAdminService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext (design-rules §3) — the Google Integration service routes through owning service interfaces");
    }

    [Fact]
    public void GoogleAdminService_HasNoDbContextFactoryConstructorParameter()
    {
        var ctor = typeof(GoogleAdminService).GetConstructors().Single();
        var factoryParam = ctor.GetParameters()
            .FirstOrDefault(p =>
                p.ParameterType.IsGenericType &&
                p.ParameterType.GetGenericTypeDefinition() == typeof(IDbContextFactory<>));

        factoryParam.Should().BeNull(
            because: "IDbContextFactory belongs behind the repository boundary, not in an Application-layer service");
    }

    [Fact]
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

    [Fact]
    public void GoogleAdminService_IsSealed()
    {
        typeof(GoogleAdminService).IsSealed.Should().BeTrue(
            because: "service implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }

    [Fact]
    public void GoogleAdminService_DoesNotReferenceGoogleSdkTypes()
    {
        // Paranoid guard: the service's assembly must not pull Google.Apis.*
        // references indirectly. Google SDK calls live behind IGoogleWorkspaceUserService
        // (which itself delegates to IWorkspaceUserDirectoryClient in Infrastructure).
        var assembly = typeof(GoogleAdminService).Assembly;
        var referenced = assembly.GetReferencedAssemblies();
        referenced
            .Should().NotContain(
                a => (a.Name ?? string.Empty).StartsWith("Google.Apis", StringComparison.Ordinal),
                because: "Humans.Application must stay free of Google SDK references");
    }
}
