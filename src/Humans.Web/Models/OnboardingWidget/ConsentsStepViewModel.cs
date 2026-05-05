using Humans.Application.Interfaces.Consent;

namespace Humans.Web.Models.OnboardingWidget;

/// <summary>
/// View model for step 3 of the onboarding widget — the per-document consent
/// list. Each <see cref="RequiredConsentRow"/> carries the document version id
/// (POST target), the display title, the review URL, and whether the user has
/// already signed it.
/// </summary>
public class ConsentsStepViewModel
{
    public required IReadOnlyList<RequiredConsentRow> RequiredConsents { get; init; }
}
