namespace Humans.Preapprovals.Authorization;

/// <summary>
/// The section's two policy names. Section-local rather than in Base's <c>PolicyNames</c>:
/// nothing outside this section gates on them.
/// </summary>
internal static class PreapprovalsPolicies
{
    /// <summary>The member pages: anyone who holds a grant, manages a program, or can apply to one; admins and the Board always.</summary>
    public const string Access = "PreapprovalsAccess";

    /// <summary>The admin index: Admin, Board, FinanceAdmin, or a manager of at least one program.</summary>
    public const string Manage = "PreapprovalsManage";
}
