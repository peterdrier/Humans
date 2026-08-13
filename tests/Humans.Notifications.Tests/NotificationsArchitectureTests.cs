using Humans.Auth.Contracts;
using System.Reflection;
using AwesomeAssertions;
using Humans.Notifications.Data;
using Humans.Notifications.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Humans.Users.Contracts;

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
    private static IEnumerable<Type> SectionTypes =>
        typeof(Section).Assembly.GetTypes().Where(t => !t.IsNested);

    // ── NotificationService ──────────────────────────────────────────────────

    // The DbContext-constructor-parameter check is covered by the generic
    // ApplicationServicesTakeNoDbContextRule for every Application service.
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
                .StartsWith("Humans.Notifications.Data", StringComparison.Ordinal));

        hasRepo.Should().BeFalse(
            because: "the meter provider is a cross-section aggregator; it should not bypass any section's public service interface (design-rules §9)");
    }

    // ── Section boundary (G5) ────────────────────────────────────────────────

    [HumansFact]
    public void SectionServicesTakeNoDbContext()
    {
        // The pre-G5 shape of this assertion — "typeof(NotificationService).Assembly does
        // not reference Microsoft.EntityFrameworkCore" — was a true statement about
        // Humans.Application and is simply false here: the section assembly holds the
        // repository and references EF on purpose. Restated on the constructor, which is
        // what it was reaching for (design §15 step 11, Calendar's finding).
        var offenders = SectionTypes
            .Where(t => t.Namespace?.StartsWith("Humans.Notifications.Services", StringComparison.Ordinal) == true)
            .SelectMany(t => t.GetConstructors())
            .SelectMany(c => c.GetParameters())
            .Where(p => typeof(DbContext).IsAssignableFrom(p.ParameterType)
                || (p.ParameterType.IsGenericType
                    && p.ParameterType.GetGenericTypeDefinition() == typeof(IDbContextFactory<>)))
            .Select(p => $"{p.Member.DeclaringType!.Name}.{p.Name}")
            .ToList();

        offenders.Should().BeEmpty(
            because: "only the repository may hold a DbContext or a context factory (peters-hard-rules)");
    }

    [HumansFact]
    public void SectionTypesLocalizeThroughTheSectionsOwnResourceSet()
    {
        // A controller that kept IStringLocalizer<SharedResource> renders every carved
        // Notification_* key as its raw key name, in all six languages, and keeps
        // compiling — _ViewImports covers the views but not the C# (design §15 step 3b).
        var offenders = SectionTypes
            .SelectMany(t => t.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            .SelectMany(c => c.GetParameters())
            .Where(p => p.ParameterType.IsGenericType
                && p.ParameterType.GetGenericTypeDefinition() == typeof(IStringLocalizer<>)
                && p.ParameterType.GetGenericArguments()[0] != typeof(NotificationsResource))
            .Select(p => $"{p.Member.DeclaringType!.Name} takes {p.ParameterType.Name}")
            .ToList();

        offenders.Should().BeEmpty(
            because: "the section's copy lives in NotificationsResource, not SharedResource");
    }

    [HumansFact]
    public void NotificationsResourceIsTheOnlyPublicTypeBesidesSection()
    {
        // HUM0034 is the build gate; this pins the intent so a Grandfathered escape or a
        // future carve-out shows up as a test failure too.
        var publicNames = typeof(Section).Assembly.GetExportedTypes()
            .Select(t => t.Name)
            .Where(n => !n.StartsWith("Baseline", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        publicNames.Should().BeEquivalentTo(["NotificationsResource", "Section"]);
    }

    // ── INotificationRepository ──────────────────────────────────────────────

    // Sealed-repository check covered by HUM0034 (section types are internal) plus
    // MA0053 (an unsealed internal class is a build error) — not by
    // IRepositoryImplementationsAreSealedRule, which sweeps Humans.Infrastructure only.
}
