using System.Globalization;
using Humans.Base.Interfaces;
using Humans.Teams.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Teams;

/// <summary>The admin dashboard's team-count tile.</summary>
internal sealed class SectionAdminTiles : ISectionAdminTiles
{
    public IEnumerable<AdminTile> Tiles() =>
    [
        new AdminTile("teams.total", "Teams", "fa-solid fa-people-group", TotalAsync, Weight: 90)
    ];

    private static async ValueTask<AdminTileValue?> TotalAsync(IServiceProvider sp, CancellationToken ct)
    {
        var count = (await sp.GetRequiredService<ITeamServiceRead>().GetTeamsAsync(ct)).Count;
        return new AdminTileValue(count.ToString(CultureInfo.CurrentCulture));
    }
}
