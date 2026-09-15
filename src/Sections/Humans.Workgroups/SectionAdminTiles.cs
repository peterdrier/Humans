using System.Globalization;
using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Services;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Workgroups;

/// <summary>
/// The admin dashboard's Workgroups tile: what the Secretary and the Board still owe.
/// BoardOrAdmin, matching every <c>/Workgroups/Admin/*</c> route.
/// </summary>
internal sealed class SectionAdminTiles : ISectionAdminTiles
{
    public IEnumerable<AdminTile> Tiles() =>
    [
        new AdminTile("workgroups.queue", "Workgroups", "fa-solid fa-people-group", QueueAsync,
            Controller: "WorkgroupsAdmin", Action: "Index",
            Policy: PolicyNames.BoardOrAdmin, Weight: 55)
    ];

    private static async ValueTask<AdminTileValue?> QueueAsync(IServiceProvider sp, CancellationToken ct)
    {
        var register = await sp.GetRequiredService<IWorkgroupService>().GetRegisterAsync(ct);
        var now = sp.GetRequiredService<IClock>().GetCurrentInstant();

        var pending = register.Count(w => w.Status is WorkgroupStatus.Applied or WorkgroupStatus.Referred);
        var awaiting = register.Sum(w => w.AwaitingDisposition().Count());
        var overdue = register.Count(w => w.IsRegistrationOverdue(now)) + register.Count(w => w.HasOverdueDisposition(now));

        // Nothing waiting on a human is nothing to show: the tile disappears rather than
        // sitting on the dashboard reading zero.
        if (pending == 0 && awaiting == 0)
            return null;

        return new AdminTileValue(
            pending.ToString(CultureInfo.CurrentCulture),
            Detail: awaiting == 0 ? null : $"{awaiting} awaiting a disposition",
            Severity: overdue > 0 ? TileSeverity.Warning : TileSeverity.Normal,
            Summary: $"{pending} working groups waiting on the Secretary");
    }
}
