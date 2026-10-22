using AwesomeAssertions;
using Microsoft.Extensions.Caching.Memory;

namespace Humans.Web.Tests.Architecture.Rules;

/// <summary>
/// Under the §15 caching pattern a service caches through a decorator or not at all; the allowlist holds the
/// reviewed exceptions.
/// </summary>
public class ApplicationServicesTakeNoMemoryCacheRule
{
    /// <summary>Add only after review, with a comment giving the caching reason.</summary>
    private static readonly HashSet<Type> Allowlist =
    [
        SectionType("Humans.Camps.Services.CampContactService"),
        SectionType("Humans.Issues.Services.IssuesService"),  // IssuesService.BadgeCacheKey(userId)
        SectionType("Humans.Consent.Services.LegalDocumentService"),
        SectionType("Humans.Notifications.Services.NotificationEmitter"),
        SectionType("Humans.Notifications.Services.NotificationInboxService"),
        SectionType("Humans.Notifications.Services.NotificationMeterProvider"),
        SectionType("Humans.Notifications.Services.NotificationService"),
        SectionType("Humans.Shifts.Services.ShiftManagementService"),
        // Nav-badge counts: each wraps a real query and evicts its own key. AdminDashboardService is not here: its
        // reads are already served by CachingUserService.
        SectionType("Humans.Feedback.Services.FeedbackService"),  // FeedbackService.BadgeCacheKey
        SectionType("Humans.Governance.Services.ApplicationDecisionService"),  // ApplicationDecisionService.VotingBadgeCacheKey(userId)
        // CacheKeys.HoldedContacts, 2-min TTL, so admin pages don't call Holded live on every load.
        SectionType("Humans.Finance.Services.Service"),
        // Rendered guide pages fetched from GitHub: not an entity read, and Guide owns no tables to decorate, so the
        // service is the cache.
        SectionType("Humans.Guide.Services.GuideContentService"),
        // Not a read cache: single-use login-token consumption and the signup-send cooldown, short-TTL state that
        // must survive between requests. Auth owns no table for it.
        SectionType("Humans.Auth.Services.MagicLinkRateLimiter")
    ];

    /// <summary>Section services are internal, so they are named by string and resolved by reflection.</summary>
    private static Type SectionType(string fullName) =>
        Extensions.SectionDiscoveryExtensions.SectionAssemblies()
            .Select(a => a.GetType(fullName, throwOnError: false))
            .FirstOrDefault(t => t is not null)
        ?? throw new InvalidOperationException(
            $"{fullName} not found in any section assembly — did the section move or rename it?");

    [HumansFact]
    public void Application_services_do_not_take_IMemoryCache_unless_allowlisted()
    {
        var assemblies = ApplicationSweepScope.Assemblies();

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
