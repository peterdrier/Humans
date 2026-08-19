using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Events;

/// <summary>Events' admin sidebar contribution — the "Event Guide" group (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Event Guide", [
            new("Dashboard",  "EventsDashboard",  "Index",      null, null, "fa-solid fa-chart-line",    PolicyNames.EventsAdminOrAdmin),
            new("Moderation", "EventsModeration", "Index",      null, null, "fa-solid fa-gavel",         PolicyNames.EventsAdminOrAdmin),
            new("Settings",   "EventsAdmin",      "Settings",   null, null, "fa-solid fa-calendar-days", PolicyNames.EventsAdminOrAdmin),
            new("Categories", "EventsAdmin",      "Categories", null, null, "fa-solid fa-tags",          PolicyNames.EventsAdminOrAdmin),
            new("Venues",     "EventsAdmin",      "Venues",     null, null, "fa-solid fa-location-dot",  PolicyNames.EventsAdminOrAdmin),
            new("Export",     "EventsExport",     "Index",      null, null, "fa-solid fa-file-export",   PolicyNames.EventsAdminOrAdmin)
        ], Weight: 60)
    ];
}
