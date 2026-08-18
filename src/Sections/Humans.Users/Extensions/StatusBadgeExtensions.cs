using Humans.Users.Contracts;

using Humans.Governance.Contracts;

namespace Humans.Users.Extensions;

/// <summary>
/// Extension methods for getting Bootstrap badge CSS classes for various status types.
/// </summary>
/// <remarks>
/// Moved out of <c>Humans.UI</c> at G5 lane 4b-i (nobodies-collective/Humans#866) — it was
/// Base's last <c>Humans.Governance.Contracts</c> site, and both of its call sites
/// (<c>_HumanSearchResults</c>, <c>UsersAdmin/AdminDetail</c>) are this section's views.
/// <c>internal</c> because nothing outside the assembly calls it; Expenses' and Budget's
/// equivalents are internal for the same reason.
/// </remarks>
internal static class StatusBadgeExtensions
{
    /// <summary>
    /// Gets the Bootstrap badge CSS class for a membership status string.
    /// Also accepts display labels projected from <see cref="UserState"/> for the admin human list.
    /// </summary>
    public static string GetMembershipStatusBadgeClass(string? status)
    {
        return status switch
        {
            nameof(UserState.Active) => "bg-success",
            nameof(UserState.Bare) => "bg-warning text-dark",
            nameof(UserState.Suspended) => "bg-danger",
            nameof(UserState.AdminSuspended) => "bg-danger",
            nameof(UserState.Rejected) => "bg-danger",
            nameof(UserState.DeletePending) or "Delete Pending" => "bg-dark",
            nameof(UserState.Merged) => "bg-secondary",
            nameof(UserState.Deleted) => "bg-secondary",
            MembershipStatusLabels.PendingApproval => "bg-warning text-dark",
            MembershipStatusLabels.PendingDeletion => "bg-dark",
            _ => "bg-secondary"
        };
    }
}
