using Humans.Application.Architecture;
using AwesomeAssertions;
using Humans.Application.Services.AuditLog;
using Humans.Camps.Services;
using Humans.Application.Services.Shifts;
using Microsoft.Extensions.Caching.Memory;

namespace Humans.Application.Tests.Architecture.Rules;

/// <summary>
/// Generic rule: no concrete Application service takes <see cref="IMemoryCache"/>
/// unless it is on the explicit allowlist of services that legitimately cache.
///
/// <para>
/// The §15 caching pattern governs which services may hold a cache. Most
/// sections use Option A (no caching) or B (caching inside the repository or
/// a dedicated decorator). A service that adds <see cref="IMemoryCache"/> to
/// its own constructor without being in the allowlist is drifting the pattern.
/// </para>
///
/// <para>
/// Allowlisted services (audited 2026-05-25, Wave B drift reconciliation):
/// <list type="bullet">
///   <item><c>Humans.Camps.Services.CampContactService</c> — contact name cache</item>
///   <item><c>IssuesService</c> — issues cache</item>
///   <item><see cref="LegalDocumentService"/> — document version cache</item>
///   <item><see cref="NotificationEmitter"/> — throttle-key cache</item>
///   <item><see cref="NotificationInboxService"/> — inbox cache</item>
///   <item><see cref="NotificationMeterProvider"/> — meter cache</item>
///   <item><see cref="NotificationService"/> — notification preferences cache</item>
///   <item><see cref="ShiftManagementService"/> — shift data cache</item>
///   <item><c>Humans.Feedback.Services.FeedbackService</c> — feedback-badge count cache (nav badges)</item>
///   <item><see cref="ApplicationDecisionService"/> — voting-badge count cache (nav badges)</item>
///   <item><c>Humans.Finance.Services.Service</c> — Holded contact-list cache (nobodies-collective/Humans#976)</item>
///   <item><c>Humans.Guide.Services.GuideContentService</c> — rendered guide-page cache</item>
///   <item><c>Humans.TicketTailor.Services.TicketTailorService</c> — vendor event-summary cache</item>
/// </list>
/// Removed (caching moved to decorators):
/// <list type="bullet">
///   <item><c>CalendarService</c> — no longer injects IMemoryCache (Wave B reconciliation)</item>
///   <item><c>CampService</c> — caching moved to CachingCampService decorator (T-06)</item>
/// </list>
/// </para>
/// </summary>
public class ApplicationServicesTakeNoMemoryCacheRule
{
    /// <summary>
    /// Services that legitimately inject <see cref="IMemoryCache"/>.
    /// Audited 2026-05-25 (Wave B drift reconciliation). Add to this set only when a new cache usage is
    /// intentional and reviewed.
    /// </summary>
    private static readonly HashSet<Type> Allowlist =
    [
        SectionType("Humans.Camps.Services.CampContactService"),
        SectionType("Humans.Issues.Services.IssuesService"),  // CacheKeys.IssuesBadge(userId)
        SectionType("Humans.Consent.Services.LegalDocumentService"),
        SectionType("Humans.Notifications.Services.NotificationEmitter"),
        SectionType("Humans.Notifications.Services.NotificationInboxService"),
        SectionType("Humans.Notifications.Services.NotificationMeterProvider"),
        SectionType("Humans.Notifications.Services.NotificationService"),
        typeof(ShiftManagementService),
        // Nav-badge count caches moved out of NavBadgesViewComponent into their
        // owning services (memory/code/viewcomponent-no-cache.md) — same inline
        // badge-count caching IssuesService/NotificationMeterProvider already do.
        // Each wraps a real repo/DB query and is evicted via the existing
        // INavBadgeCacheInvalidator / IVotingBadgeCacheInvalidator. (The review
        // count is NOT here — its read is already cache-served by CachingUserService,
        // so AdminDashboardService stays cache-free; double-caching it would be §4b.)
        SectionType("Humans.Feedback.Services.FeedbackService"),  // CacheKeys.FeedbackBadgeCount
        SectionType("Humans.Governance.Services.ApplicationDecisionService"),  // CacheKeys.VotingBadge(userId)
        // CacheKeys.HoldedContacts — 2-min TTL so /Finance/Creditors and /Expenses/{id} don't
        // call Holded live on every admin page load (nobodies-collective/Humans#976).
        SectionType("Humans.Finance.Services.Service"),
        // guide:<stem> — the rendered HTML of 28 markdown files fetched from GitHub, held
        // with a sliding TTL from Guide:CacheTtlHours. Not an entity read, so §15's
        // repository/decorator options do not apply: the service *is* the cache, and
        // Guide owns no tables to decorate. Pre-existing and documented as accepted in the
        // section's invariants doc; it entered this sweep at Guide's G5 move, because the
        // rule scans Humans.Application plus the section assemblies and Guide's code used
        // to sit in Humans.Infrastructure, which neither covers.
        SectionType("Humans.Guide.Services.GuideContentService"),
        // Not a cache at all: DevPersonaSeeder *evicts* one. Three calls to
        // MemoryCacheExtensions.InvalidateUserAccess(userId) after it changes a dev persona's
        // roles or team memberships — the same call Shell's GateTerminalAccountSeeder makes,
        // and the eviction helper is an IMemoryCache extension, so there is no invalidator
        // interface to inject instead. It entered this sweep at Development's G5 move for
        // Guide's reason, one step further: the code is unchanged and used to sit in
        // Humans.Web/Infrastructure, which the sweep covers neither before nor after. A move
        // can put code into a sweep as easily as out of one.
        SectionType("Humans.Development.Services.DevPersonaSeeder"),
        // CacheKeys.TicketEventSummary(eventId) — the vendor's capacity/sold/remaining counters,
        // held 15 minutes so the ticket dashboard does not call TicketTailor on every render.
        // It is the connector's own response cache, not an entity read, so §15's repository and
        // decorator options do not apply; Services/Stores/ does not fit either, because this is
        // the adapter itself rather than a store the section holds. Third sighting of Guide's
        // rule after Development's: the code is unchanged and used to sit in
        // Humans.Infrastructure/Services, which this sweep covers neither before nor after — it
        // entered scope at the TicketTailor adapter's carve into its own section.
        SectionType("Humans.TicketTailor.Services.TicketTailorService")
    ];

    /// <summary>
    /// A G5 section's service is <c>internal</c> to its own assembly
    /// (nobodies-collective/Humans#866), so it cannot be named with <c>typeof</c> here.
    /// Resolved by reflection instead — the same mechanism
    /// <c>GdprExportDependencyInjectionTests</c> uses, so there is one way to name a moved
    /// section type from this project rather than two.
    /// </summary>
    private static Type SectionType(string fullName) =>
        Web.Extensions.SectionDiscoveryExtensions.SectionAssemblies()
            .Select(a => a.GetType(fullName, throwOnError: false))
            .FirstOrDefault(t => t is not null)
        ?? throw new InvalidOperationException(
            $"{fullName} not found in any section assembly — did the section move or rename it?");

    [HumansFact]
    public void Application_services_do_not_take_IMemoryCache_unless_allowlisted()
    {
        // Scan Humans.Application *and* every G5 section assembly. Scoping this to
        // Humans.Application alone would let a section quietly leave the sweep the moment it
        // moved out — the §10 silent-drop shape: the rule keeps passing while covering less.
        // Anchor on a type that cannot leave Humans.Application. AuditLogService was the
        // anchor until its own G5 move, at which point typeof(...).Assembly silently became
        // the section assembly and this rule stopped scanning Base at all — the same
        // silent-drop the widening below exists to prevent, arriving through the anchor
        // rather than the filter. DontFixAttribute is Base's architecture vocabulary.
        var assemblies = new[] { typeof(DontFixAttribute).Assembly }
            .Concat(Web.Extensions.SectionDiscoveryExtensions.SectionAssemblies());

        var violations = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.Namespace?.StartsWith("Humans.Application.Services.", StringComparison.Ordinal) == true
                     || (t.Namespace?.StartsWith("Humans.", StringComparison.Ordinal) == true
                         && t.Namespace.EndsWith(".Services", StringComparison.Ordinal)))
            .Where(t => !Allowlist.Contains(t))
            .SelectMany(t =>
                t.GetConstructors()
                    .SelectMany(c => c.GetParameters())
                    .Where(p => typeof(IMemoryCache).IsAssignableFrom(p.ParameterType))
                    .Select(_ => $"{t.FullName}: injects IMemoryCache but is not in the allowlist"))
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        violations.Should().BeEmpty(
            because: "application services must not hold IMemoryCache unless they are on the " +
                     "§15-allowlist; add to Allowlist only after explicit review and add a " +
                     "comment explaining the caching rationale");
    }
}
