using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Humans.Users.Contracts;

namespace Humans.CityPlanning.Contracts;

/// <summary>
/// Live cursor presence for the barrio and container maps. Owned by the City Planning
/// section since G5 lane 4b-ii (nobodies-collective/Humans#866); it used to sit in
/// <c>Humans.UI</c> because the hub has to be <c>public</c> — Shell's
/// <c>app.MapHub&lt;CityPlanningHub&gt;("/hubs/city-planning")</c> names the concrete type —
/// and a section is internal by default (HUM0034).
/// <c>Contracts/</c> is the carve-out that rule exists for: a deliberate surface Shell and
/// this section's own <c>CityPlanningApiController</c> (<c>IHubContext&lt;CityPlanningHub&gt;</c>,
/// to broadcast polygon saves) both depend on. Base could never own it once
/// <c>Humans.UI</c> retires, because the section names the type and a section may not
/// reference Shell.
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
