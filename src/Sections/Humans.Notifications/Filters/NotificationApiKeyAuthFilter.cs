using System.Security.Claims;
using Humans.Auth.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace Humans.Notifications.Filters;

internal sealed class NotificationApiOptions
{
    public const string SectionName = "NotificationApi";

    public string ApiKey { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;
}

/// <summary>
/// Authenticates the single configured notifications API consumer and installs the
/// configured human, including their active roles, as the request principal.
/// </summary>
internal sealed class NotificationApiKeyAuthFilter(
    IOptions<NotificationApiOptions> options,
    IUserServiceRead users,
    IRoleAssignmentService roles) : IAsyncAuthorizationFilter
{
    internal const string ApiKeyHeaderName = "X-Api-Key";
    internal const string AuthenticationScheme = "NotificationApiKey";

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.ApiKey)
            || !context.HttpContext.Request.Headers.TryGetValue(ApiKeyHeaderName, out var presented)
            || !string.Equals(presented.ToString(), settings.ApiKey, StringComparison.Ordinal)
            || !Guid.TryParse(settings.UserId, out var configuredUserId)
            || configuredUserId == Guid.Empty)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        var user = await users.GetUserInfoAsync(
            configuredUserId, context.HttpContext.RequestAborted);
        if (user is null || user.IsTombstone)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        var assignments = await roles.GetActiveForUserAsync(
            user.Id, context.HttpContext.RequestAborted);
        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            .. assignments.Select(a => new Claim(ClaimTypes.Role, a.RoleName)),
        ];

        context.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(claims, AuthenticationScheme));
    }
}
