using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Rideshare;

/// <summary>Rideshare's admin sidebar group: the year's settings + stats, and the day roster.</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Rideshare", [
            new("Settings & stats", "RideshareAdmin", "Index", null, null, "fa-solid fa-car",          PolicyNames.AdminOnly),
            new("Day roster",       "RideshareAdmin", "Day",   null, null, "fa-solid fa-calendar-day", PolicyNames.AdminOnly)
        ], Weight: 65)
    ];
}
