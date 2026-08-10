using Humans.UI.Filters;
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

public class ApiKeyAuthFilter(IOptions<FeedbackApiSettings> settings)
    : ApiKeyAuthFilterBase(settings.Value.ApiKey);

public class IssuesApiKeyAuthFilter(IOptions<IssuesApiSettings> settings)
    : ApiKeyAuthFilterBase(settings.Value.ApiKey);

public class LogApiKeyAuthFilter(IOptions<LogApiSettings> settings)
    : ApiKeyAuthFilterBase(settings.Value.ApiKey);

public class AgentApiKeyAuthFilter(IOptions<AgentApiSettings> settings)
    : ApiKeyAuthFilterBase(settings.Value.ApiKey);
