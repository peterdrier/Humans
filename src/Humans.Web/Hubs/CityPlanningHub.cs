using System.Collections.Concurrent;
using Humans.Application.Interfaces.CitiPlanning;
using Humans.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;

namespace Humans.Web.Hubs;

[Authorize]
public class CityPlanningHub(ICityPlanningService cityPlanningService, UserManager<User> userManager) : Hub
{
    private static readonly ConcurrentDictionary<string, string> _displayNames = new(StringComparer.Ordinal);

    public override async Task OnConnectedAsync()
    {
        var userId = userManager.GetUserId(Context.User!);
        if (userId != null)
        {
            var burnerName = await cityPlanningService.GetUserDisplayNameAsync(Guid.Parse(userId));
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
