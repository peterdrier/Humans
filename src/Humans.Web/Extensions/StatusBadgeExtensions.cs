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
    /// Gets the Bootstrap badge CSS class for an application status string.
    /// </summary>
    public static string GetApplicationStatusBadgeClass(string? status)
    {
        return status switch
        {
            "Submitted" => "bg-primary",
            "Approved" => "bg-success",
            "Rejected" => "bg-danger",
            _ => "bg-secondary"
        };
    }

    /// <summary>
    /// Gets the Bootstrap badge CSS class for a membership status string.
    /// </summary>
    public static string GetMembershipStatusBadgeClass(string? status)
    {
        return status switch
        {
            MembershipStatusLabels.Active => "bg-success",
            MembershipStatusLabels.PendingApproval => "bg-warning text-dark",
            MembershipStatusLabels.MissingConsents => "bg-info text-dark",
            MembershipStatusLabels.IncompleteSignup => "bg-secondary",
            MembershipStatusLabels.Suspended => "bg-danger",
            MembershipStatusLabels.PendingDeletion => "bg-dark",
            _ => "bg-secondary"
        };
    }
}
