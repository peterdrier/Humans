namespace Humans.Expenses.Services;

/// <summary>
/// The <c>EntityType</c> discriminators Expenses writes to the audit log.
/// </summary>
/// <remarks>
/// <para>
/// These are <b>persisted strings</b>, matched by exact equality when the audit log is read back
/// (<c>IAuditLogService</c> filters on <c>e.EntityType == entityType</c>), so they are a data
/// contract with every row already in the database — not a view of the current CLR type names.
/// </para>
/// <para>
/// Expenses arrived at G5 already writing bare literals rather than <c>nameof(ExpenseReport)</c>,
/// so it never had Store's orphaned-rows bug. Naming them here is the prep for the step 5 prefix
/// drop that has not happened yet: a rename-at-scale over the section is exactly the pass that
/// would otherwise sweep nine loose <c>"ExpenseReport"</c> strings along with the type, and the
/// failure is silent — no build error, no test failure, just an audit panel that reads back empty.
/// Never regenerate these from <c>nameof</c>.
/// </para>
/// </remarks>
internal static class AuditEntityTypes
{
    public const string Report = "ExpenseReport";
}
