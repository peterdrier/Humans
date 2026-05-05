namespace Humans.Web.Models.OnboardingWidget;

/// <summary>
/// View model for step 3 of the onboarding widget — Consents. Shows the next
/// unsigned required document inline (full content, not a link) so the user
/// reads what they're agreeing to. After they sign, the dispatcher routes
/// back here to show the next document, or Home when all are signed.
/// </summary>
public class ConsentsStepViewModel
{
    public required Guid DocumentVersionId { get; init; }
    public required string DocumentName { get; init; }
    public required string VersionNumber { get; init; }
    public required Dictionary<string, string> Content { get; init; }
    public string? ChangesSummary { get; init; }

    /// <summary>1-based position of the document being shown (e.g. "Document 2 of 3").</summary>
    public required int CurrentIndex { get; init; }

    public required int TotalRequired { get; init; }
}
