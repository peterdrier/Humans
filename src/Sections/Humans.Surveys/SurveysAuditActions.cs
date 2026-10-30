using Humans.AuditLog.Contracts;

namespace Humans.Surveys;

/// <summary>The audit actions Surveys writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class SurveysAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition SurveyCreated = new("SurveyCreated");

    public static readonly AuditActionDefinition SurveyUpdated = new("SurveyUpdated");

    public static readonly AuditActionDefinition SurveyOpened = new("SurveyOpened");

    public static readonly AuditActionDefinition SurveyClosed = new("SurveyClosed");

    public static readonly AuditActionDefinition SurveyInvitesSent = new("SurveyInvitesSent");

    public static readonly AuditActionDefinition SurveyReminderSent = new("SurveyReminderSent");

    // An author submits a Draft for Board/Admin review. Approval opens and sends in one step; rejection returns
    // it to Draft with a note.
    public static readonly AuditActionDefinition SurveySubmittedForApproval = new("SurveySubmittedForApproval");

    public static readonly AuditActionDefinition SurveyApproved = new("SurveyApproved");

    public static readonly AuditActionDefinition SurveyRejected = new("SurveyRejected");

    public static readonly AuditActionDefinition SurveyResponsesExported = new("SurveyResponsesExported");
}
