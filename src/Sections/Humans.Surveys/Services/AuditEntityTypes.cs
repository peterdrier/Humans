namespace Humans.Surveys.Services;

/// <summary>
/// Persisted strings, matched exactly when the audit log is read back: never rename them or regenerate them from
/// <c>nameof</c>, or the audit panel silently reads back empty.
/// </summary>
internal static class AuditEntityTypes
{
    public const string Survey = "Survey";

    /// <summary>The <c>jobName</c> recorded on actorless reminder-send audit rows.</summary>
    public const string ReminderJob = "SurveyService";
}
