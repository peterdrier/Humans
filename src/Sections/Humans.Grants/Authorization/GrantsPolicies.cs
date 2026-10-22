namespace Humans.Grants.Authorization;

internal static class GrantsPolicies
{
    /// <summary>Admin, Board, or a manager of at least one program.</summary>
    public const string Manage = "GrantsManage";
}
