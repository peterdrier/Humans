using Humans.Audit.Contracts;

namespace Humans.Surveys;

/// <summary>The audit actions Surveys writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class SurveysAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition SurveyCreated = new("SurveyCreated", AuditKept.Forever);

    public static readonly AuditActionDefinition SurveyUpdated = new("SurveyUpdated", AuditKept.Forever);

    public static readonly AuditActionDefinition SurveyOpened = new("SurveyOpened", AuditKept.Forever);

    public static readonly AuditActionDefinition SurveyClosed = new("SurveyClosed", AuditKept.Forever);

    public static readonly AuditActionDefinition SurveyInvitesSent = new("SurveyInvitesSent", AuditKept.Forever);

    public static readonly AuditActionDefinition SurveyReminderSent = new("SurveyReminderSent", AuditKept.Forever);

    // An author submits a Draft for Board/Admin review. Approval opens and sends in one step; rejection returns
    // it to Draft with a note.
    public static readonly AuditActionDefinition SurveySubmittedForApproval = new("SurveySubmittedForApproval", AuditKept.Forever);

    public static readonly AuditActionDefinition SurveyApproved = new("SurveyApproved", AuditKept.Forever);

    public static readonly AuditActionDefinition SurveyRejected = new("SurveyRejected", AuditKept.Forever);

    public static readonly AuditActionDefinition SurveyResponsesExported = new("SurveyResponsesExported", AuditKept.Forever);
}
