using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace Humans.Web.Filters;

public class FeedbackApiSettings
{
    public const string SectionName = "FeedbackApi";
    public string ApiKey { get; set; } = string.Empty;
}

public class LogApiSettings
{
    public string ApiKey { get; set; } = string.Empty;
}

public abstract class ApiKeyAuthFilterBase : IAuthorizationFilter
{
    private const string ApiKeyHeaderName = "X-Api-Key";
    private readonly string _apiKey;

    protected ApiKeyAuthFilterBase(string apiKey)
    {
        _apiKey = apiKey;
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

public class ApiKeyAuthFilter(IOptions<FeedbackApiSettings> settings)
    : ApiKeyAuthFilterBase(settings.Value.ApiKey);

public class LogApiKeyAuthFilter(IOptions<LogApiSettings> settings)
    : ApiKeyAuthFilterBase(settings.Value.ApiKey);

public class NotificationApiSettings
{
    public string ApiKey { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
}

public class NotificationApiKeyAuthFilter(IOptions<NotificationApiSettings> settings)
    : ApiKeyAuthFilterBase(settings.Value.ApiKey);
