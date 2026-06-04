using Humans.Domain.Enums;

namespace Humans.Web.Models.Survey;

/// <summary>
/// Per-session state of the survey answering wizard, JSON-serialised into <c>HttpContext.Session</c>
/// keyed by token (see <see cref="SurveyWizardSession"/>). HTTP/session types stay in the Web layer.
/// Task 4.2 extends usage (page navigation, answer capture); 4.1 establishes the shape and the
/// intro/start step. Answers are keyed by <c>QuestionId.ToString()</c> (Guid object keys don't
/// round-trip through JSON cleanly; string keys do).
/// </summary>
internal sealed class SurveyWizardState
{
    public Guid SurveyId { get; set; }
    public Guid? InvitationId { get; set; }   // the token's invitation — all invited tiers (drives Started/Completed funnel flags)
    public Guid? UserId { get; set; }          // the token's user — all invited tiers; the RESPONSE columns are written only for Identified (see submit)
    public Guid? DraftResponseId { get; set; } // Identified draft only (set by StartIdentifiedDraftAsync)
    public ResponseAnonymity Anonymity { get; set; }
    public SurveyInputMethod InputMethod { get; set; } = SurveyInputMethod.UserSpecificLink;
    public string Culture { get; set; } = "en";
    public int CurrentPage { get; set; }
    public bool Started { get; set; }
    public Dictionary<string, SurveyWizardAnswer> Answers { get; set; } = new(StringComparer.Ordinal); // key = QuestionId.ToString()
}

/// <summary>One captured answer in the wizard session.</summary>
internal sealed class SurveyWizardAnswer
{
    public List<string> SelectedOptionValues { get; set; } = [];
    public string? TextValue { get; set; }
    public int? RatingValue { get; set; }
}
