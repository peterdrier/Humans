namespace Humans.Surveys.Services;

/// <summary>
/// Persisted strings, matched exactly when the audit log is read back: never rename them or regenerate them from
/// <c>nameof</c>, or the audit panel silently reads back empty.
/// </summary>
internal static class AuditEntityTypes
{
    public const string Survey = "Survey";

    /// <summary>
    /// The <c>relatedEntityType</c> on approval-workflow entries about the survey's author, so the
    /// audit filters that key on "User" pick them up for that author's GDPR export.
    /// </summary>
    public const string User = "User";

    /// <summary>The <c>jobName</c> recorded on actorless reminder-send audit rows.</summary>
    public const string ReminderJob = "SurveyService";
}
