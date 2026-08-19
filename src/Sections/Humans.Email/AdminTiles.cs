using System.Globalization;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Email;

/// <summary>The admin dashboard's outbox-total tile.</summary>
public sealed class AdminTiles : ISectionAdminTiles
{
    public IEnumerable<AdminTile> Tiles() =>
    [
        new AdminTile("email.outbox", "Emails", "fa-solid fa-envelope", OutboxAsync, Weight: 110)
    ];

    private static async ValueTask<AdminTileValue?> OutboxAsync(IServiceProvider sp)
    {
        // recentMessageCount: 0 — the tile wants the count, not the messages.
        var stats = await sp.GetRequiredService<IEmailOutboxServiceRead>().GetOutboxStatsAsync(0);
        return new AdminTileValue(stats.TotalCount.ToString("N0", CultureInfo.CurrentCulture), Detail: "outbox total");
    }
}
