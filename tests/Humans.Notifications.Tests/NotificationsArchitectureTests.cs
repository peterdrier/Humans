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

    // The DI-cycle invariant — RoleAssignmentService (Auth) and TeamService
    // (Teams) inject INotificationEmitter rather than INotificationService, and
    // NotificationEmitter injects nothing that leads back to either — is
    // enforced by ValidateOnBuild / ValidateScopes at host startup
    // (Humans.Web/Program.cs), not by a test here. A test for it would assert
    // the absence of constructor parameters, which
    // memory/architecture/no-tests-for-absences.md rejects: the list of things
    // a type lacks is unbounded and such a test can only fail on the deliberate
    // edit that would update it in the same commit.

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
