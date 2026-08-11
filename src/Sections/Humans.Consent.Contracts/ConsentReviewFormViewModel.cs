namespace Humans.Consent.Contracts;

/// <summary>
/// Renders a single legal-document review body — tabbed multilingual content,
/// "I agree" checkbox, submit button, and the JS that re-localises the
/// checkbox text when the language tab changes. Shared by /Consent/Review and
/// the onboarding widget Consents step so the consent UX is identical
/// regardless of where the user encounters it.
/// </summary>
/// <remarks>
/// On the contracts leaf rather than in the section's <c>Models/</c> because the widget
/// step is Onboarding's view over Consent's form: Shell constructs this model and renders
/// the section's <c>_ConsentReviewBody</c> partial, which resolves by name across
/// application parts. Moving the markup instead (design §15 step 3b's fourth direction)
/// is not available — the step view binds <c>ConsentsStepViewModel</c>, a Shell type a
/// section cannot name.
/// </remarks>
public class ConsentReviewFormViewModel
{
    public required Guid DocumentVersionId { get; init; }
    public required Dictionary<string, string> Content { get; init; }
    public string? ChangesSummary { get; init; }

    /// <summary>POST target action name.</summary>
    public required string SubmitAction { get; init; }

    /// <summary>POST target controller name. Null = same controller as current view.</summary>
    public string? SubmitController { get; init; }

    /// <summary>Localised text for the submit button.</summary>
    public required string SubmitButtonText { get; init; }

    /// <summary>Optional secondary link rendered alongside the submit button.</summary>
    public string? CancelHref { get; init; }
    public string? CancelText { get; init; }
}
