using Humans.Base.Filters;
using Microsoft.Extensions.Options;

namespace Humans.Debug;

internal sealed class LogApiSettings
{
    public string ApiKey { get; set; } = string.Empty;
}

internal sealed class LogApiKeyAuthFilter(IOptions<LogApiSettings> settings)
    : ApiKeyAuthFilterBase(settings.Value.ApiKey);
