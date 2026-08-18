using Humans.Base.Filters;
using Microsoft.Extensions.Options;

namespace Humans.Agent.Filters;

internal sealed class AgentApiSettings
{
    public const string SectionName = "AgentApi";
    public string ApiKey { get; set; } = string.Empty;
}

internal sealed class AgentApiKeyAuthFilter(IOptions<AgentApiSettings> settings)
    : ApiKeyAuthFilterBase(settings.Value.ApiKey);
