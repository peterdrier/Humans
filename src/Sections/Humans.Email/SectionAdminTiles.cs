using System.Globalization;
using Humans.Base.Interfaces;
using Humans.Email.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Email;

internal sealed class SectionAdminTiles : ISectionAdminTiles
{
    public IEnumerable<AdminTile> Tiles() =>
    [
        new AdminTile("email.outbox", "Emails", "fa-solid fa-envelope", OutboxAsync, Weight: 110)
    ];

    private static async ValueTask<AdminTileValue?> OutboxAsync(IServiceProvider sp, CancellationToken ct)
    {
        // recentMessageCount: 0 — the tile wants the count, not the messages.
        var stats = await sp.GetRequiredService<IEmailOutboxManagement>().GetOutboxStatsAsync(0, ct);
        return new AdminTileValue(stats.TotalCount.ToString("N0", CultureInfo.CurrentCulture), Detail: "outbox total");
    }
}
