using Humans.UI.Filters;
using Microsoft.Extensions.Options;

namespace Humans.Surveys.Filters;

public class SurveyApiSettings
{
    public const string SectionName = "SurveyApi";
    public string ApiKey { get; set; } = string.Empty;
}

public class SurveyApiKeyAuthFilter(IOptions<SurveyApiSettings> settings)
    : ApiKeyAuthFilterBase(settings.Value.ApiKey);
