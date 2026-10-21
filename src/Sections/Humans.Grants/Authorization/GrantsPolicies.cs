namespace Humans.Grants.Authorization;

/// <summary>
/// The section's one policy name. Section-local rather than in Base's <c>PolicyNames</c>:
/// nothing outside this section gates on it.
/// </summary>
internal static class GrantsPolicies
{
    /// <summary>The admin pages: Admin, Board, or a manager of at least one program.</summary>
    public const string Manage = "GrantsManage";
}
