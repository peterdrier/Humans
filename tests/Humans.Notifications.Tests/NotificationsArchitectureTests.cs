using AwesomeAssertions;
using Humans.Notifications.Data;
using Humans.Notifications.Services;

namespace Humans.Notifications.Tests;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for the
/// Notifications section — migrated per issue #550, moved to its own project at G5
/// (nobodies-collective/Humans#866).
///
/// <para>
/// Notifications chose <b>Option A</b> (no caching decorator, no dict cache):
/// in-app dispatch is fire-and-forget and reads go through the inbox service.
/// Nav-badge counts are cached inside <c>NotificationInboxService.GetUnreadBadgeCountsAsync</c>
/// via short-TTL <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/> (§15).
/// The same rationale used by Users (#243), Governance (#242), Budget (#544),
/// City Planning (#543), and Audit Log (#552) when they skipped the decorator.
/// </para>
/// </summary>
public class NotificationsArchitectureTests
{
    // ── NotificationService ──────────────────────────────────────────────────

    // The DbContext-constructor-parameter check is covered by the generic
    // ApplicationServicesTakeNoDbContextRule for every Application service.

    [HumansFact]
    public void NotificationEmitter_TakesNothingThatReachesBackOut()
    {
        // This is the edge that keeps the DI graph acyclic. RoleAssignmentService
        // (Auth) and TeamService (Teams) inject INotificationEmitter rather than
        // INotificationService, and NotificationEmitter injects only this
        // section's repository plus preferences, clock, cache and logger — so
        // their edge into Notifications terminates here. Give the emitter a
        // dependency that leads back to Auth or Teams and the graph closes,
        // which ValidateOnBuild only reports at startup.
        //
        // The other half of the invariant — that RoleAssignmentService and
        // TeamService keep depending on INotificationEmitter and never on
        // INotificationService — lives in those sections and is not pinned by a
        // test today.
        var ctor = typeof(NotificationEmitter).GetConstructors().Single();
        var paramTypeNames = ctor.GetParameters().Select(p => p.ParameterType.Name).ToList();

        paramTypeNames.Should().NotContain("IRoleAssignmentService");
        paramTypeNames.Should().NotContain("ITeamService");
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

    // ── INotificationRepository ──────────────────────────────────────────────

    // Sealed-repository check covered by HUM0034 (section types are internal) plus
    // MA0053 (an unsealed internal class is a build error) — not by
    // IRepositoryImplementationsAreSealedRule, which sweeps Humans.Infrastructure only.
}
