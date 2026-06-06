using Humans.Domain.Constants;
using Humans.Domain.Enums;

namespace Humans.Web.Extensions;

/// <summary>
/// Extension methods for getting Bootstrap badge CSS classes for various status types.
/// </summary>
public static class StatusBadgeExtensions
{
    /// <summary>
    /// Gets the Bootstrap badge CSS class for an application status.
    /// </summary>
    public static string GetBadgeClass(this ApplicationStatus status)
    {
        return status switch
        {
            ApplicationStatus.Submitted => "bg-primary",
            ApplicationStatus.Approved => "bg-success",
            ApplicationStatus.Rejected => "bg-danger",
            _ => "bg-secondary"
        };
    }

    /// <summary>
    /// Gets the Bootstrap badge CSS class for an application status (nullable).
    /// </summary>
    public static string GetBadgeClass(this ApplicationStatus? status)
    {
        return status.HasValue ? status.Value.GetBadgeClass() : "bg-secondary";
    }

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
