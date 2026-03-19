using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace Humans.Web.Filters;

public class FeedbackApiSettings
{
    public const string SectionName = "FeedbackApi";
    public string ApiKey { get; set; } = string.Empty;
}

public class ApiKeyAuthFilter : IAuthorizationFilter
{
    private const string ApiKeyHeaderName = "X-Api-Key";
    private readonly string _apiKey;

    public ApiKeyAuthFilter(IOptions<FeedbackApiSettings> settings)
    {
        _apiKey = settings.Value.ApiKey;
    }

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            context.Result = new StatusCodeResult(503); // Not configured
            return;
        }

        if (!context.HttpContext.Request.Headers.TryGetValue(ApiKeyHeaderName, out var providedKey)
            || !string.Equals(providedKey, _apiKey, StringComparison.Ordinal))
        {
            context.Result = new UnauthorizedResult(); // 401
        }
    }
}
