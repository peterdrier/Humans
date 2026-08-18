using Humans.Base.Filters;
using Microsoft.Extensions.Options;

namespace Humans.Feedback.Filters;

/// <summary>
/// <c>FEEDBACK_API_KEY</c> for <c>/api/feedback</c>. The shared header check lives in
/// <see cref="ApiKeyAuthFilterBase"/> in <c>Humans.UI</c> — a section cannot reference
/// <c>Humans.Web</c>, and the base names no section vocabulary (design §15 step 6).
/// </summary>
internal sealed class FeedbackApiSettings
{
    public const string SectionName = "FeedbackApi";
    public string ApiKey { get; set; } = string.Empty;
}

internal sealed class FeedbackApiKeyAuthFilter(IOptions<FeedbackApiSettings> settings)
    : ApiKeyAuthFilterBase(settings.Value.ApiKey);
