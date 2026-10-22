using Humans.Users.Contracts;

using Humans.Governance.Contracts;

namespace Humans.Users.Extensions;

internal static class StatusBadgeExtensions
{
    /// <summary>Also accepts the <see cref="UserState"/> display labels of the admin human list.</summary>
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
