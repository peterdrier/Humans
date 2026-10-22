namespace Humans.Consent.Contracts;

/// <summary>
/// Shared by /Consent/Review and the onboarding widget's Consents step so the consent UX is identical:
/// the widget builds this model and renders the section's <c>_ConsentReviewBody</c> partial by name.
/// </summary>
public class ConsentReviewFormViewModel
{
    public required Guid DocumentVersionId { get; init; }
    public required Dictionary<string, string> Content { get; init; }
    public string? ChangesSummary { get; init; }

    public required string SubmitAction { get; init; }

    /// <summary>Null means the current view's controller.</summary>
    public string? SubmitController { get; init; }

    public required string SubmitButtonText { get; init; }

    public string? CancelHref { get; init; }
    public string? CancelText { get; init; }
}
