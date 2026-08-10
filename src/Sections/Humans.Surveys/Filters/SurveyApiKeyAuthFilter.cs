using Humans.UI.Filters;
using Microsoft.Extensions.Options;

namespace Humans.Surveys.Filters;

internal sealed class SurveyApiSettings
{
    public const string SectionName = "SurveyApi";
    public string ApiKey { get; set; } = string.Empty;
}

internal sealed class SurveyApiKeyAuthFilter(IOptions<SurveyApiSettings> settings)
    : ApiKeyAuthFilterBase(settings.Value.ApiKey);
