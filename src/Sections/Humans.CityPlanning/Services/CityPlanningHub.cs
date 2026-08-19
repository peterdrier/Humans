using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Humans.Users.Contracts;

namespace Humans.CityPlanning.Services;

/// <summary>
/// Live cursor presence for the barrio and container maps. Owned by the City Planning
/// section since G5 lane 4b-ii (nobodies-collective/Humans#866); it used to sit in
/// <c>Humans.UI</c> and had to be <c>public</c> because Shell's
/// <c>app.MapHub&lt;CityPlanningHub&gt;("/hubs/city-planning")</c> named the concrete type.
/// That call moved into this section's own <c>SectionEndpoints : ISectionEndpoints</c>
/// (nobodies-collective/Humans#1075), so the hub is <c>internal</c> like the rest of the
/// section (HUM0034) — its only consumers are <c>SectionEndpoints</c> and
/// <c>CityPlanningApiController</c>'s <c>IHubContext&lt;CityPlanningHub&gt;</c>, both in this
/// assembly.
/// </summary>
[Authorize]
internal sealed class CityPlanningHub(IUserServiceRead userService, UserManager<User> userManager) : Hub
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
