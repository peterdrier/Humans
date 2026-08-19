using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Cantina;

/// <summary>Cantina's admin sidebar contribution — the "Cantina" group (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Cantina", [
            new("Roster", "Cantina", "Roster", null, null, "fa-solid fa-utensils", PolicyNames.CantinaAdminOrAdmin)
        ], Weight: 40)
    ];
}
