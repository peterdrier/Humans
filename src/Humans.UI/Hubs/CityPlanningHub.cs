using System.Collections.Concurrent;
using Humans.Application.Interfaces.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Humans.Users.Contracts;

namespace Humans.UI.Hubs;

/// <summary>
/// Live cursor presence for the barrio and container maps. In Humans.UI rather than the
/// City Planning section for the same reason <c>ApiKeyAuthFilterBase</c> is: Shell's
/// <c>app.MapHub&lt;CityPlanningHub&gt;("/hubs/city-planning")</c> names the concrete type,
/// and a type in a <c>[assembly: Section("…")]</c> assembly cannot be public (HUM0034).
/// It names no City Planning vocabulary at all — it relays a connection id, a display name
/// and a lat/lng — so the move is the Gate <c>&lt;vc:human-search&gt;</c> shape, not a
/// promotion of section types into Base. The section injects
/// <c>IHubContext&lt;CityPlanningHub&gt;</c> to broadcast polygon saves.
/// </summary>
[Authorize]
public sealed class CityPlanningHub(IUserServiceRead userService, UserManager<User> userManager) : Hub
{
    private static readonly ConcurrentDictionary<string, string> _displayNames = new(StringComparer.Ordinal);

    public override async Task OnConnectedAsync()
    {
        var userId = userManager.GetUserId(Context.User!);
        if (userId != null)
        {
            var burnerName = (await userService.GetUserInfoAsync(Guid.Parse(userId)))?.BurnerName;
            _displayNames[Context.ConnectionId] = !string.IsNullOrWhiteSpace(burnerName)
                ? burnerName
                : Context.User?.Identity?.Name ?? "Unknown";
        }
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Called by clients to broadcast their cursor position.
    /// Relayed to all other connected clients.
    /// </summary>
    public async Task UpdateCursor(double lat, double lng)
    {
        var displayName = _displayNames.GetValueOrDefault(Context.ConnectionId, Context.User?.Identity?.Name ?? "Unknown");
        await Clients.Others.SendAsync("CursorMoved", Context.ConnectionId, displayName, lat, lng);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _displayNames.TryRemove(Context.ConnectionId, out _);
        await Clients.Others.SendAsync("CursorLeft", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
