using System.ComponentModel;
using System.Reflection;
using System.Security.Claims;
using Humans.Backdoor.Authentication;
using Humans.Backdoor.Services;
using Humans.Base.Extensions;
using Humans.Users.Contracts;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Humans.Backdoor.Mcp;

/// <summary>
/// Proves the connection: which server answered (address, environment, version), who the caller is signed in as, and
/// how. The server half is what tells production from staging.
/// </summary>
[McpServerToolType]
[BackdoorScope(BackdoorScope.AdminAndBoard)]
internal sealed class HelloTool(IUserService users, IHttpContextAccessor http, IHostEnvironment environment)
{
    [McpServerTool(Name = "hello", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Checks the connection: says which Humans server answered (its address, environment and version) and who you are signed in as (your name, your roles and how you signed in).")]
    public async Task<HelloResult> HelloAsync(
        RequestContext<CallToolRequestParams> context, CancellationToken ct)
    {
        var user = context.User ?? throw new InvalidOperationException("The MCP endpoint let an anonymous call through.");
        var userId = user.GetUserId() ?? throw new InvalidOperationException("The signed-in caller has no user id.");
        var info = await users.GetUserInfoAsync(userId, ct);
        var name = info?.BurnerName ?? string.Empty;
        var request = http.HttpContext?.Request;
        var server = request is null ? string.Empty : $"{request.Scheme}://{request.Host}";
        var version = Version();

        return new HelloResult(
            $"Hello {name}! You are connected to Humans {environment.EnvironmentName} at {server}, version {version}.",
            server,
            environment.EnvironmentName,
            version,
            userId,
            name,
            [.. user.FindAll(ClaimTypes.Role).Select(c => c.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            SignedInWith(user));
    }

    /// <summary>The version and commit <c>/api/version</c> reports, as one string.</summary>
    private static string Version() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    private static string SignedInWith(ClaimsPrincipal user) =>
        user.Identity?.AuthenticationType switch
        {
            BackdoorApiKeyHandler.SchemeName => "API key",
            var other => other ?? "unknown",
        };
}

internal sealed record HelloResult(
    string Message,
    string Server,
    string Environment,
    string Version,
    Guid UserId,
    string Name,
    IReadOnlyList<string> Roles,
    string SignedInWith);
