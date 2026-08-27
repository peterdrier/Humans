namespace Humans.Surveys.Services;

/// <summary>
/// The <c>EntityType</c> and job-name strings Surveys writes to the audit log.
/// </summary>
/// <remarks>
/// <para>
/// These are <b>persisted strings</b>, matched by exact equality when the audit log is read back
/// (<c>IAuditLogService</c> filters on <c>e.EntityType == entityType</c>), so they are a data
/// contract with every row already in the database — not a view of the current CLR type names.
/// </para>
/// <para>
/// Leaving them derived from
/// <c>nameof</c> means the next rename over this section silently changes what the code writes
/// <em>and</em> what it queries, in lockstep, with no build error and no failing test: the audit
/// panel just reads back empty. Pinning them as literals is what makes that rename schema-inert,
/// the way the EF <c>ToTable</c> calls already are
/// (memory/code/type-name-as-persisted-string.md). Never regenerate these from <c>nameof</c>.
/// </para>
/// </remarks>
internal static class AuditEntityTypes
{
    public const string Survey = "Survey";

    /// <summary>The <c>jobName</c> recorded on actorless reminder-send audit rows.</summary>
    public const string ReminderJob = "SurveyService";
}
