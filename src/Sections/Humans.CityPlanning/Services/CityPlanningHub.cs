using System.Collections.Concurrent;
using Humans.Base.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Humans.Users.Contracts;

namespace Humans.CityPlanning.Services;

/// <summary>
/// Live cursor presence for the barrio map. Keep it <c>internal</c>: a <c>MapHub</c> call by concrete type from Shell
/// would force it public.
/// </summary>
[Authorize]
[Authorize(Policy = PolicyNames.AppAccess)]
internal sealed class CityPlanningHub(IUserService userService) : Hub
{
    private static readonly ConcurrentDictionary<string, string> _displayNames = new(StringComparer.Ordinal);

    public override async Task OnConnectedAsync()
    {
        // SignalR's default IUserIdProvider reads the NameIdentifier claim, the same one Identity's GetUserId reads.
        if (Guid.TryParse(Context.UserIdentifier, out var userId))
        {
            var burnerName = (await userService.GetUserInfoAsync(userId))?.BurnerName;
            _displayNames[Context.ConnectionId] = !string.IsNullOrWhiteSpace(burnerName)
                ? burnerName
                : Context.User?.Identity?.Name ?? "Unknown";
        }
        await base.OnConnectedAsync();
    }

    // The JS client invokes the wire name 'UpdateCursor'; keep it stable across the C# rename.
    [HubMethodName("UpdateCursor")]
    public async Task UpdateCursorAsync(double lat, double lng)
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
