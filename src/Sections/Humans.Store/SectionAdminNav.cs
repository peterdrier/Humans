using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Store;

/// <summary>Store's admin sidebar group (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Store", [
            new("Catalog",     "StoreAdmin", "Catalog",    null, null, "fa-solid fa-tags",           PolicyNames.StoreCatalogAdmin),
            new("Summary",     "StoreAdmin", "Summary",    null, null, "fa-solid fa-chart-column",   PolicyNames.StoreCatalogAdmin),
            new("Payments",    "StoreAdmin", "Payments",   null, null, "fa-solid fa-credit-card",    PolicyNames.StoreCatalogAdmin),
            new("Order years", "StoreAdmin", "OrderYears", null, null, "fa-solid fa-calendar-check", PolicyNames.StoreCatalogAdmin)
        ])
    ];
}
