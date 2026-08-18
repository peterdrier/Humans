using Humans.Base.Filters;
using Microsoft.Extensions.Options;

namespace Humans.Issues.Filters;

internal sealed class IssuesApiSettings
{
    public const string SectionName = "IssuesApi";
    public string ApiKey { get; set; } = string.Empty;
}

/// <summary>
/// <c>X-Api-Key</c> gate on <c>/api/issues</c>. Derives from the shared
/// <see cref="ApiKeyAuthFilterBase"/> in <c>Humans.UI</c> — a section cannot reference
/// <c>Humans.Web</c>, and the base names no section vocabulary (design §15 step 6).
/// </summary>
internal sealed class IssuesApiKeyAuthFilter(IOptions<IssuesApiSettings> settings)
    : ApiKeyAuthFilterBase(settings.Value.ApiKey);
