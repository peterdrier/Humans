using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Resource-based authorization requirement for IBAN access.
/// Covers three access scenarios:
/// <list type="bullet">
///   <item><description>Self — viewing/editing your own IBAN via the expense-report flow.</description></item>
///   <item><description>FinanceAdmin in a report context — the report must be non-Draft and non-Withdrawn.</description></item>
///   <item><description>Admin on an admin page — the admin user page surfaces the IBAN with audit logging.</description></item>
/// </list>
/// </summary>
public sealed class IbanAccessRequirement(Guid targetUserId, Guid? reportId = null, bool isAdminPageContext = false)
    : IAuthorizationRequirement
{
    /// <summary>
    /// The user ID whose IBAN is being accessed. Used for self-access check.
    /// </summary>
    public Guid TargetUserId { get; } = targetUserId;

    /// <summary>
    /// Optional: the report being viewed. When provided, FinanceAdmin access is
    /// granted only if the report is in a non-Draft / non-Withdrawn state.
    /// Null means no report context (e.g., Admin user-page access).
    /// </summary>
    public Guid? ReportId { get; } = reportId;

    /// <summary>
    /// When true, this is an Admin accessing the user admin page.
    /// Admin-on-admin-page access is granted without a report context.
    /// </summary>
    public bool IsAdminPageContext { get; } = isAdminPageContext;
}
