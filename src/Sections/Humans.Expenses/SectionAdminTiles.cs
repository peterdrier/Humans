using System.Globalization;
using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.Expenses.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Expenses;

/// <summary>
/// The admin dashboard's expense-report tile. Policy-gated below /Admin's AnyAdminRole,
/// mirroring the expense pages' own tighter policy.
/// </summary>
internal sealed class SectionAdminTiles : ISectionAdminTiles
{
    public IEnumerable<AdminTile> Tiles() =>
    [
        new AdminTile("expenses.reports", "Expense reports", "fa-solid fa-receipt", ReportsAsync,
            Policy: PolicyNames.FinanceAdminOrAdmin, Weight: 130)
    ];

    private static async ValueTask<AdminTileValue?> ReportsAsync(IServiceProvider sp, CancellationToken ct)
    {
        var reports = await sp.GetRequiredService<IExpenseReportServiceRead>().GetAllAsync(ct);
        return new AdminTileValue(
            reports.Count.ToString("N0", CultureInfo.CurrentCulture),
            Detail: $"€{reports.Sum(r => r.Total):N0} total, all statuses");
    }
}
