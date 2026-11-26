using Humans.Backdoor.Models;
using Humans.Backdoor.OAuth;
using Humans.Backdoor.Services;
using Humans.Base.Controllers;
using Humans.Users.Contracts;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using System.Security.Claims;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Humans.Backdoor.Controllers;

/// <summary>
/// The "Sign in with Humans" approval page. OpenIddict has already checked the client, its return address and PKCE
/// before either action runs. Behind the normal cookie sign-in; the person is always asked, never approved silently.
/// </summary>
[Route(Section.AuthorizePath)]
internal sealed class BackdoorOAuthController(
    IBackdoorConnectionService connections,
    IBackdoorApiKeyService keys,
    IUserService users) : HumansControllerBase(users)
{
    [HttpGet("")]
    public async Task<IActionResult> Authorize(CancellationToken ct)
    {
        var (request, client) = ReadRequest();
        if (request is null || client is null) return BadRequest();

        var userId = GetCurrentUserId();
        if (userId is null || !await keys.IsEligibleAsync(userId.Value, ct)) return Forbid();

        if (!AsksForThisServer(request)) return Refuse(Errors.InvalidTarget);

        return View(new BackdoorAuthorizeViewModel(
            client.DisplayName,
            new Uri(client.RedirectUri).Host,
            Request.Query.Select(p => new KeyValuePair<string, string>(p.Key, p.Value.ToString())).ToList()));
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Decide(string? decision, CancellationToken ct)
    {
        var (request, client) = ReadRequest();
        if (request is null || client is null) return BadRequest();

        var userId = GetCurrentUserId();
        if (userId is null) return Forbid();

        if (!string.Equals(decision, "allow", StringComparison.Ordinal)) return Refuse(Errors.AccessDenied);
        if (!AsksForThisServer(request)) return Refuse(Errors.InvalidTarget);

        var identity = await connections.ApproveAsync(userId.Value, client, Section.McpResource(Request), ct);
        if (identity is null) return Refuse(Errors.AccessDenied);

        return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private (OpenIddictRequest? Request, BackdoorOAuthClient? Client) ReadRequest()
    {
        var request = HttpContext.GetOpenIddictServerRequest();
        return (request, BackdoorOAuthClients.Find(request?.ClientId));
    }

    /// <summary>A token asked for another resource would be made out to it; this server only issues its own.</summary>
    private bool AsksForThisServer(OpenIddictRequest request) =>
        request.Resources is not { Length: > 0 } resources
        || resources.All(r => string.Equals(r, Section.McpResource(Request), StringComparison.OrdinalIgnoreCase));

    /// <summary>Sends the client back an OAuth error, so the app shows it instead of hanging.</summary>
    private ForbidResult Refuse(string error) =>
        Forbid(
            new AuthenticationProperties(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            }),
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
}
