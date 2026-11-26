using System.ComponentModel;
using System.Security.Claims;
using Humans.Backdoor.Authentication;
using Humans.Backdoor.Services;
using Humans.Base.Extensions;
using Humans.Users.Contracts;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Humans.Backdoor.Mcp;

/// <summary>Proves the connection: who the caller is signed in as, and how.</summary>
[McpServerToolType]
[BackdoorScope(BackdoorScope.AdminAndBoard)]
internal sealed class HelloTool(IUserService users)
{
    [McpServerTool(Name = "hello", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Says who you are signed in to Humans as: your name, your roles and how you signed in. Use it to check the connection works.")]
    public async Task<HelloResult> HelloAsync(
        RequestContext<CallToolRequestParams> context, CancellationToken ct)
    {
        var user = context.User ?? throw new InvalidOperationException("The MCP endpoint let an anonymous call through.");
        var userId = user.GetUserId() ?? throw new InvalidOperationException("The signed-in caller has no user id.");
        var info = await users.GetUserInfoAsync(userId, ct);

        return new HelloResult(
            userId,
            info?.BurnerName ?? string.Empty,
            [.. user.FindAll(ClaimTypes.Role).Select(c => c.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            SignedInWith(user));
    }

    private static string SignedInWith(ClaimsPrincipal user) =>
        user.Identity?.AuthenticationType switch
        {
            BackdoorApiKeyHandler.SchemeName => "API key",
            var other => other ?? "unknown",
        };
}

internal sealed record HelloResult(Guid UserId, string Name, IReadOnlyList<string> Roles, string SignedInWith);
