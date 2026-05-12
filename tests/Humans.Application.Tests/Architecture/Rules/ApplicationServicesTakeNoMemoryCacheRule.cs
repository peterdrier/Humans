using AwesomeAssertions;
using Humans.Application.Services.AuditLog;
using Humans.Application.Services.Calendar;
using Humans.Application.Services.Camps;
using Humans.Application.Services.Issues;
using Humans.Application.Services.Legal;
using Humans.Application.Services.Notifications;
using Humans.Application.Services.Shifts;
using Humans.Application.Services.Tickets;
using Humans.Testing;
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
/// Allowlisted services (audited 2026-05-12, align/auditlog Phase 2 Step 5):
/// <list type="bullet">
///   <item><see cref="CalendarService"/> — short-TTL cache for Google Calendar data</item>
///   <item><see cref="CampContactService"/> — contact name cache</item>
///   <item><see cref="CampService"/> — camp listing cache</item>
///   <item><see cref="IssuesService"/> — issues cache</item>
///   <item><see cref="LegalDocumentService"/> — document version cache</item>
///   <item><see cref="NotificationEmitter"/> — throttle-key cache</item>
///   <item><see cref="NotificationInboxService"/> — inbox cache</item>
///   <item><see cref="NotificationMeterProvider"/> — meter cache</item>
///   <item><see cref="NotificationService"/> — notification preferences cache</item>
///   <item><see cref="ShiftManagementService"/> — shift data cache</item>
///   <item><see cref="TicketQueryService"/> — ticket query cache</item>
///   <item><see cref="TicketSyncService"/> — sync state cache</item>
/// </list>
/// </para>
///
/// This rule generalises per-section tests such as
/// <c>AuditLogService_HasNoIMemoryCacheConstructorParameter</c> — those can
/// be deleted in Phase 3 once this generic rule is confirmed green.
/// </summary>
public class ApplicationServicesTakeNoMemoryCacheRule
{
    /// <summary>
    /// Services that legitimately inject <see cref="IMemoryCache"/>.
    /// Audited 2026-05-12. Add to this set only when a new cache usage is
    /// intentional and reviewed.
    /// </summary>
    private static readonly HashSet<Type> Allowlist = new()
    {
        typeof(CalendarService),
        typeof(CampContactService),
        typeof(CampService),
        typeof(IssuesService),
        typeof(LegalDocumentService),
        typeof(NotificationEmitter),
        typeof(NotificationInboxService),
        typeof(NotificationMeterProvider),
        typeof(NotificationService),
        typeof(ShiftManagementService),
        typeof(TicketQueryService),
        typeof(TicketSyncService),
    };

    [HumansFact]
    public void Application_services_do_not_take_IMemoryCache_unless_allowlisted()
    {
        var appAssembly = typeof(AuditLogService).Assembly;

        var violations = appAssembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.Namespace?.StartsWith("Humans.Application.Services.", StringComparison.Ordinal) == true)
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
