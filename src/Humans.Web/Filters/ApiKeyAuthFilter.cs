using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace Humans.Web.Filters;

public class FeedbackApiSettings
{
    public const string SectionName = "FeedbackApi";
    public string ApiKey { get; set; } = string.Empty;
}

public class IssuesApiSettings
{
    public const string SectionName = "IssuesApi";
    public string ApiKey { get; set; } = string.Empty;
}

public class LogApiSettings
{
    public string ApiKey { get; set; } = string.Empty;
}

public class AgentApiSettings
{
    public const string SectionName = "AgentApi";
    public string ApiKey { get; set; } = string.Empty;
}

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

public class ApiKeyAuthFilter(IOptions<FeedbackApiSettings> settings)
    : ApiKeyAuthFilterBase(settings.Value.ApiKey);

public class IssuesApiKeyAuthFilter(IOptions<IssuesApiSettings> settings)
    : ApiKeyAuthFilterBase(settings.Value.ApiKey);

public class LogApiKeyAuthFilter(IOptions<LogApiSettings> settings)
    : ApiKeyAuthFilterBase(settings.Value.ApiKey);

public class AgentApiKeyAuthFilter(IOptions<AgentApiSettings> settings)
    : ApiKeyAuthFilterBase(settings.Value.ApiKey);
