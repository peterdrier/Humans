using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Holded;

/// <summary>Holded's contribution to the shared "Money" admin group (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Money", [
            new("Holded", "Holded", "Index", null, null, "fa-solid fa-book", PolicyNames.FinanceAdminOrAdmin, Weight: 20)
        ], Weight: 50)
    ];
}
