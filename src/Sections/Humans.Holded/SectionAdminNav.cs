using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Holded;

internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Holded", [
            new("Holded", "Holded", "Index", null, null, "fa-solid fa-book", PolicyNames.FinanceAdminOrAdmin)
        ])
    ];
}
