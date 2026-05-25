using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using NotificationService = Humans.Application.Services.Notifications.NotificationService;
using NotificationInboxService = Humans.Application.Services.Notifications.NotificationInboxService;
using NotificationMeterProvider = Humans.Application.Services.Notifications.NotificationMeterProvider;

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
public class NotificationsArchitectureTests
{
    // ── NotificationService ──────────────────────────────────────────────────

    // The DbContext-constructor-parameter check is covered by the generic
    // ApplicationServicesTakeNoDbContextRule for every Application service.
    // Repository-takes check covered by IRepositoryImplementationsAreSealedRule.
    // Service-namespace check covered by HUM0012.

    [HumansFact]
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

    [HumansFact]
    public void NotificationInboxService_TakesRepositoryAndUserService()
    {
        // Display-name stitching runs through IUserServiceRead.GetUserInfosAsync rather
        // than a cross-domain .Include(nr => nr.User) chain (design-rules §6).
        var ctor = typeof(NotificationInboxService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(INotificationRepository));
        paramTypes.Should().Contain(p => p.Name == "IUserServiceRead");
    }

    // ── NotificationMeterProvider ────────────────────────────────────────────

    [HumansFact]
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

    // Sealed-repository check is covered by the generic
    // IRepositoryImplementationsAreSealedRule across every repository.

}
