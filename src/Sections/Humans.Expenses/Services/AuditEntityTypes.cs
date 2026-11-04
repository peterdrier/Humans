namespace Humans.Expenses.Services;

/// <summary>
/// Persisted strings matched by exact equality when the audit log is read back. Never derive them from
/// <c>nameof</c>: a rename would silently empty every audit panel.
/// </summary>
internal static class AuditEntityTypes
{
    public const string Report = "ExpenseReport";

    /// <summary>Users' <c>Profile</c>, which is internal to Users, so it is spelled here.</summary>
    public const string Profile = "Profile";
}
