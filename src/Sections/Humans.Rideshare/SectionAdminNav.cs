using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Rideshare;

internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Rideshare", [
            new("Settings & stats", "RideshareAdmin", "Index", null, null, "fa-solid fa-car",          PolicyNames.RideshareAdminOrAdmin),
            new("Day roster",       "RideshareAdmin", "Day",   null, null, "fa-solid fa-calendar-day", PolicyNames.RideshareAdminOrAdmin)
        ])
    ];
}
