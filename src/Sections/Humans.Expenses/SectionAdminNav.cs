using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Expenses;

internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Expenses", [
            // The finance-admin door to the review queue, so it keeps the admin policy. Members reach the same page
            // from /Expenses.
            new("Review", "Expenses", "Review", null, null, "fa-solid fa-magnifying-glass-dollar", PolicyNames.FinanceAdminOrAdmin)
        ])
    ];
}
