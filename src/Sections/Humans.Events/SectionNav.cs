using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Events;

/// <summary>
/// Member top-nav contribution — was the Events dropdown in Shell's <c>_Layout.cshtml</c>,
/// gated on the <c>Features:Events</c> flag plus <see cref="PolicyNames.AppAccess"/>. The two
/// admin sub-groups (guide dashboard/moderation/export, then guide settings/categories/venues)
/// are further gated on <see cref="PolicyNames.EventsAdminOrAdmin"/>, each behind its own
/// divider so the group appears only when its items do.
/// </summary>
internal sealed class SectionNav : ISectionNav
{
    public IEnumerable<MemberNavItem> Items() =>
    [
        new(
            "Events",
            Policy: PolicyNames.AppAccess,
            Weight: 50,
            Visible: (sp, _) => sp.GetRequiredService<IConfiguration>().GetValue<bool>("Features:Events"),
            Children:
            [
                new("Browse Events", Controller: "Events", Action: "Browse"),
                new("My Submissions", Controller: "Events", Action: "MySubmissions"),
                new("My Schedule", Controller: "Events", Action: "Schedule"),
                new("", Divider: true, Policy: PolicyNames.EventsAdminOrAdmin),
                new("Guide Dashboard", Controller: "EventsDashboard", Action: "Index", Policy: PolicyNames.EventsAdminOrAdmin),
                new("Moderate Events", Controller: "EventsModeration", Action: "Index", Policy: PolicyNames.EventsAdminOrAdmin),
                new("Export Guide", Controller: "EventsExport", Action: "Index", Policy: PolicyNames.EventsAdminOrAdmin),
                new("", Divider: true, Policy: PolicyNames.EventsAdminOrAdmin),
                new("Guide Settings", Controller: "EventsAdmin", Action: "Settings", Policy: PolicyNames.EventsAdminOrAdmin),
                new("Guide Categories", Controller: "EventsAdmin", Action: "Categories", Policy: PolicyNames.EventsAdminOrAdmin),
                new("Guide Venues", Controller: "EventsAdmin", Action: "Venues", Policy: PolicyNames.EventsAdminOrAdmin)
            ])
    ];
}
