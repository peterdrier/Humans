using Humans.Base.Filters;
using Microsoft.Extensions.Options;

namespace Humans.Web.Filters;

public class LogApiSettings
{
    public string ApiKey { get; set; } = string.Empty;
}

public class LogApiKeyAuthFilter(IOptions<LogApiSettings> settings)
    : ApiKeyAuthFilterBase(settings.Value.ApiKey);
