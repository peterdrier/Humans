using Humans.UI.Filters;
using Microsoft.Extensions.Options;

namespace Humans.Web.Filters;

public class IssuesApiSettings
{
    public const string SectionName = "IssuesApi";
    public string ApiKey { get; set; } = string.Empty;
}

public class LogApiSettings
{
    public string ApiKey { get; set; } = string.Empty;
}

public class IssuesApiKeyAuthFilter(IOptions<IssuesApiSettings> settings)
    : ApiKeyAuthFilterBase(settings.Value.ApiKey);

public class LogApiKeyAuthFilter(IOptions<LogApiSettings> settings)
    : ApiKeyAuthFilterBase(settings.Value.ApiKey);
