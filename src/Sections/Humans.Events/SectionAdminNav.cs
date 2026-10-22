using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Events;

internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Events", [
            new("Dashboard",  "EventsDashboard",  "Index",      null, null, "fa-solid fa-chart-line",    PolicyNames.EventsAdminOrAdmin),
            new("Moderation", "EventsModeration", "Index",      null, null, "fa-solid fa-gavel",         PolicyNames.EventsAdminOrAdmin),
            new("Categories", "EventsAdmin",      "Categories", null, null, "fa-solid fa-tags",          PolicyNames.EventsAdminOrAdmin),
            new("Venues",     "EventsAdmin",      "Venues",     null, null, "fa-solid fa-location-dot",  PolicyNames.EventsAdminOrAdmin),
            new("Export",     "EventsExport",     "Index",      null, null, "fa-solid fa-file-export",   PolicyNames.EventsAdminOrAdmin)
        ])
    ];
}
