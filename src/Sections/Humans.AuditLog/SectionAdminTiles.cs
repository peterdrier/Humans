using System.Globalization;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.AuditLog;

/// <summary>The admin dashboard's audit-event count.</summary>
internal sealed class SectionAdminTiles : ISectionAdminTiles
{
    public IEnumerable<AdminTile> Tiles() =>
    [
        new AdminTile("auditlog.total", "Audit events", "fa-solid fa-list-check", TotalAsync, Weight: 100)
    ];

    // Reuse of the existing page read: page size 1, no filter — TotalCount is the tile.
    private static async ValueTask<AdminTileValue?> TotalAsync(IServiceProvider sp, CancellationToken ct)
    {
        var total = (await sp.GetRequiredService<IAuditViewerService>().GetPageAsync(null, 1, 1, ct)).TotalCount;
        return new AdminTileValue(total.ToString("N0", CultureInfo.CurrentCulture), Detail: "every automated action, logged");
    }
}
