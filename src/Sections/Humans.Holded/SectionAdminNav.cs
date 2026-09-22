using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Holded;

/// <summary>Holded's admin sidebar group (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Holded", [
            new("Holded", "Holded", "Index", null, null, "fa-solid fa-book", PolicyNames.FinanceAdminOrAdmin)
        ])
    ];
}
