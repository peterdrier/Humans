using System.Globalization;
using Humans.Base.Interfaces;
using Humans.Shifts.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Shifts;

/// <summary>The admin dashboard's shift-coverage tile.</summary>
internal sealed class SectionAdminTiles : ISectionAdminTiles
{
    public IEnumerable<AdminTile> Tiles() =>
    [
        new AdminTile("shifts.coverage", "Shifts staffed", "fa-solid fa-calendar-check", CoverageAsync, Weight: 40)
    ];

    private static async ValueTask<AdminTileValue?> CoverageAsync(IServiceProvider sp, CancellationToken ct)
    {
        var (filled, total, ratio) = await sp.GetRequiredService<IShiftManagementServiceRead>().GetOverallCoverageAsync(ct);
        var percent = total > 0 ? (int)Math.Round(ratio * 100) : 0;
        return total > 0
            ? new AdminTileValue(
                filled.ToString(CultureInfo.CurrentCulture),
                Detail: $"{percent}% of slots filled",
                Secondary: $"/ {total}",
                Summary: $"{percent}% shift coverage")
            : new AdminTileValue(
                "",
                Detail: $"{percent}% of slots filled",
                Secondary: "—",
                Summary: $"{percent}% shift coverage");
    }
}
