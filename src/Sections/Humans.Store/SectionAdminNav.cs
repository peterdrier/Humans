using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Store;

/// <summary>Store's contribution to the shared "Money" admin group (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Money", [
            new("Store catalog",  "StoreAdmin", "Catalog",  null, null, "fa-solid fa-tags",         PolicyNames.StoreCatalogAdmin, Weight: 30),
            new("Store summary",  "StoreAdmin", "Summary",  null, null, "fa-solid fa-chart-column", PolicyNames.StoreCatalogAdmin, Weight: 40),
            new("Store payments", "StoreAdmin", "Payments", null, null, "fa-solid fa-credit-card",  PolicyNames.StoreCatalogAdmin, Weight: 50)
        ], Weight: 50)
    ];
}
