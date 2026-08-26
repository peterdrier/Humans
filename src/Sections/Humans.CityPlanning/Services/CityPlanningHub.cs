using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Humans.Users.Contracts;

namespace Humans.CityPlanning.Services;

/// <summary>
/// Live cursor presence for the barrio and container maps. <c>internal</c> like the rest
/// of the section (HUM0034): its only consumers are this section's
/// <c>SectionEndpoints</c>, which maps it, and <c>CityPlanningApiController</c>'s
/// <c>IHubContext&lt;CityPlanningHub&gt;</c> — both in this assembly. Keep it that way;
/// a <c>MapHub</c> call by concrete type from Shell would force it public again.
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
