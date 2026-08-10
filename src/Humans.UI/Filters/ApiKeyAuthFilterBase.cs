using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Humans.UI.Filters;

/// <summary>
/// Shared <c>X-Api-Key</c> header check for the key-authed read APIs (Feedback, Issues, Log,
/// Agent, Surveys). 503 when no key is configured, 401 when the header is missing or wrong.
/// </summary>
/// <remarks>
/// In Base rather than Shell because a section that moves into its own project at G5
/// (nobodies-collective/Humans#866) owns its <c>&lt;Section&gt;ApiKeyAuthFilter</c> and its
/// settings type, and a section cannot reference <c>Humans.Web</c>. The mechanism carries no
/// section vocabulary — it is the same shape as <see cref="Controllers.ApiControllerBase"/>,
/// which already lives here — so it is genuinely shared machinery, not a promoted section type
/// (memory/architecture/section-project-cycle-fix.md draws that line).
/// </remarks>
public abstract class ApiKeyAuthFilterBase(string apiKey) : IAuthorizationFilter
{
    private const string ApiKeyHeaderName = "X-Api-Key";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            context.Result = new StatusCodeResult(503); // Not configured
            return;
        }

        if (!context.HttpContext.Request.Headers.TryGetValue(ApiKeyHeaderName, out var providedKey)
            || !string.Equals(providedKey, apiKey, StringComparison.Ordinal))
        {
            context.Result = new UnauthorizedResult(); // 401
        }
    }
}
