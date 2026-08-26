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
/// Naming them here keeps a rename-at-scale over the section from sweeping the loose
/// <c>"ExpenseReport"</c> strings along with the type. That failure is silent — no build error,
/// no test failure, just an audit panel that reads back empty. Never regenerate these from
/// <c>nameof</c>.
/// </para>
/// </remarks>
internal static class AuditEntityTypes
{
    public const string Report = "ExpenseReport";

    /// <summary>
    /// The IBAN set/remove entries name Users' <c>Profile</c>, which this section cannot spell
    /// since it went internal to <c>Humans.Users</c> (nobodies-collective/Humans#1051). Same
    /// remedy Onboarding already applied for the same rows.
    /// </summary>
    public const string Profile = "Profile";

    /// <summary>
    /// The <c>relatedEntityType</c> on entries about a member, so the audit filters that key on
    /// "User" pick them up for that member's GDPR export — an entry whose subject is only reachable
    /// through its actor never reaches the person it is about.
    /// </summary>
    public const string User = "User";
}
