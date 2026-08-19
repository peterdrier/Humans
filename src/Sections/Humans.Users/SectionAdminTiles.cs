using System.Globalization;
using Humans.Base.Interfaces;
using Humans.Shifts.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Users;

/// <summary>The admin dashboard's people counts — Users formats them, Shell only places them.</summary>
internal sealed class SectionAdminTiles : ISectionAdminTiles
{
    public IEnumerable<AdminTile> Tiles() =>
    [
        new AdminTile("users.total", "Total users", "fa-solid fa-users", TotalAsync, Weight: 10),
        new AdminTile("users.profiles", "Active (has profile)", "fa-solid fa-id-card", ProfilesAsync, Weight: 20),
        new AdminTile("users.tickets", "Ticket holders", "fa-solid fa-ticket", TicketHoldersAsync, Weight: 30)
    ];

    private static async ValueTask<AdminTileValue?> TotalAsync(IServiceProvider sp)
    {
        var count = (await Snapshot(sp)).Count;
        return new AdminTileValue(count.ToString(CultureInfo.CurrentCulture), Summary: $"{count} users");
    }

    private static async ValueTask<AdminTileValue?> ProfilesAsync(IServiceProvider sp)
    {
        var count = (await Snapshot(sp)).Count(u => u.IsActive);
        return new AdminTileValue(count.ToString(CultureInfo.CurrentCulture), Summary: $"{count} with profile");
    }

    private static async ValueTask<AdminTileValue?> TicketHoldersAsync(IServiceProvider sp)
    {
        var activeEvent = await sp.GetRequiredService<IBurnSettingsService>().GetActiveAsync();
        var count = activeEvent is { Year: > 0 }
            ? (await Snapshot(sp)).Count(u => u.HasTicketForYear(activeEvent.Year))
            : 0;
        return new AdminTileValue(count.ToString(CultureInfo.CurrentCulture), Summary: $"{count} with ticket");
    }

    private static async Task<IReadOnlyCollection<UserInfo>> Snapshot(IServiceProvider sp) =>
        await sp.GetRequiredService<IUserServiceRead>().GetAllUserInfosAsync();
}
