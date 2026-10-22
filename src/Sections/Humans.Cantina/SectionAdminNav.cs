using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Cantina;

internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Cantina", [
            new("Roster", "Cantina", "Roster", null, null, "fa-solid fa-utensils", PolicyNames.CantinaAdminOrAdmin)
        ])
    ];
}
