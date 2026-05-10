using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;
using NotificationService = Humans.Application.Services.Notifications.NotificationService;
using NotificationInboxService = Humans.Application.Services.Notifications.NotificationInboxService;
using NotificationMeterProvider = Humans.Application.Services.Notifications.NotificationMeterProvider;
using Humans.Infrastructure.Repositories.Notifications;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for the
/// Notifications section — migrated per issue #550.
///
/// <para>
/// Notifications chose <b>Option A</b> (no caching decorator, no dict cache):
/// in-app dispatch is fire-and-forget and reads go through the inbox service
/// whose nav-badge counts are already cached at the view-component layer via
/// short-TTL <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/>.
/// The same rationale used by Users (#243), Governance (#242), Budget (#544),
/// City Planning (#543), and Audit Log (#552) when they skipped the decorator.
/// </para>
/// </summary>
public partial class ArchitectureShapeTests
{
    [HumansFact]
    public void NotificationsArchitecture_contracts_hold()
    {
        NotificationService_LivesInHumansApplicationServicesNotificationsNamespace();
        NotificationService_HasNoDbContextConstructorParameter();
        NotificationService_TakesRepository();
        NotificationService_TakesRecipientResolver_NotDbContext();
        NotificationInboxService_LivesInHumansApplicationServicesNotificationsNamespace();
        NotificationInboxService_HasNoDbContextConstructorParameter();
        NotificationInboxService_TakesRepositoryAndUserService();
        NotificationMeterProvider_LivesInHumansApplicationServicesNotificationsNamespace();
        NotificationMeterProvider_HasNoDbContextConstructorParameter();
        NotificationMeterProvider_TakesCrossSectionInterfaces();
        NotificationMeterProvider_TakesNoRepositoryDependency();
        INotificationRepository_LivesInApplicationInterfacesRepositoriesNamespace();
        NotificationRepository_IsSealed();
    }

    // ── NotificationService ──────────────────────────────────────────────────
    public void NotificationService_LivesInHumansApplicationServicesNotificationsNamespace()
    {
        typeof(NotificationService).Namespace
            .Should().Be("Humans.Application.Services.Notifications",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }
    public void NotificationService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(NotificationService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use INotificationRepository instead (design-rules §3)");
    }
    public void NotificationService_TakesRepository()
    {
        var ctor = typeof(NotificationService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(INotificationRepository));
    }
    public void NotificationService_TakesRecipientResolver_NotDbContext()
    {
        // The NotificationService reaches teams and role holders via a thin
        // recipient-resolver adapter rather than directly injecting
        // ITeamService/IRoleAssignmentService — those services inject
        // INotificationService in the other direction, so a direct dependency
        // here closes a circular DI graph that trips ValidateOnBuild at
        // startup. The resolver exists solely to break that cycle.
        var ctor = typeof(NotificationService).GetConstructors().Single();
        var paramTypeNames = ctor.GetParameters().Select(p => p.ParameterType.Name).ToList();

        paramTypeNames.Should().Contain("INotificationRecipientResolver");
        paramTypeNames.Should().NotContain("ITeamService");
        paramTypeNames.Should().NotContain("IRoleAssignmentService");
    }

    // ── NotificationInboxService ─────────────────────────────────────────────
    public void NotificationInboxService_LivesInHumansApplicationServicesNotificationsNamespace()
    {
        typeof(NotificationInboxService).Namespace
            .Should().Be("Humans.Application.Services.Notifications");
    }
    public void NotificationInboxService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(NotificationInboxService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use INotificationRepository instead");
    }
    public void NotificationInboxService_TakesRepositoryAndUserService()
    {
        // Display-name stitching runs through IUserService.GetByIdsAsync rather
        // than a cross-domain .Include(nr => nr.User) chain (design-rules §6).
        var ctor = typeof(NotificationInboxService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(INotificationRepository));
        paramTypes.Should().Contain(p => p.Name == "IUserService");
    }

    // ── NotificationMeterProvider ────────────────────────────────────────────
    public void NotificationMeterProvider_LivesInHumansApplicationServicesNotificationsNamespace()
    {
        typeof(NotificationMeterProvider).Namespace
            .Should().Be("Humans.Application.Services.Notifications");
    }
    public void NotificationMeterProvider_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(NotificationMeterProvider).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "the meter provider must reach every non-owned table via its owning section service (design-rules §2c)");
    }
    public void NotificationMeterProvider_TakesCrossSectionInterfaces()
    {
        // The meter provider computes badge counts by calling into each owning
        // section service — IProfileService, IUserService, IGoogleSyncService,
        // ITeamService, ITicketSyncService, IApplicationDecisionService — never
        // reading the underlying tables directly.
        var ctor = typeof(NotificationMeterProvider).GetConstructors().Single();
        var paramTypeNames = ctor.GetParameters().Select(p => p.ParameterType.Name).ToList();

        paramTypeNames.Should().Contain("IProfileService");
        paramTypeNames.Should().Contain("IUserService");
        paramTypeNames.Should().Contain("IGoogleSyncService");
        paramTypeNames.Should().Contain("ITeamService");
        paramTypeNames.Should().Contain("ITicketSyncService");
        paramTypeNames.Should().Contain("IApplicationDecisionService");
    }
    public void NotificationMeterProvider_TakesNoRepositoryDependency()
    {
        // The meter provider does not own notifications/notification_recipients
        // reads either — those stay with the inbox service. It is purely an
        // aggregator across other sections' count methods.
        var ctor = typeof(NotificationMeterProvider).GetConstructors().Single();
        var hasRepo = ctor.GetParameters()
            .Any(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Repositories", StringComparison.Ordinal));

        hasRepo.Should().BeFalse(
            because: "the meter provider is a cross-section aggregator; it should not bypass any section's public service interface (design-rules §9)");
    }

    // ── INotificationRepository ──────────────────────────────────────────────
    public void INotificationRepository_LivesInApplicationInterfacesRepositoriesNamespace()
    {
        typeof(INotificationRepository).Namespace
            .Should().Be("Humans.Application.Interfaces.Repositories",
                because: "repository interfaces live in Humans.Application.Interfaces.Repositories per design-rules §3");
    }
    public void NotificationRepository_IsSealed()
    {
        var repoType = typeof(NotificationRepository);

        repoType.IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }
}
