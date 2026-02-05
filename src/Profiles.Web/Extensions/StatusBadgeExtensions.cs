using Profiles.Domain.Enums;

namespace Profiles.Web.Extensions;

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
            ApplicationStatus.UnderReview => "bg-info",
            ApplicationStatus.Approved => "bg-success",
            ApplicationStatus.Rejected => "bg-danger",
            ApplicationStatus.Withdrawn => "bg-secondary",
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
            "UnderReview" => "bg-info",
            "Approved" => "bg-success",
            "Rejected" => "bg-danger",
            "Withdrawn" => "bg-secondary",
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
            "Active" => "bg-success",
            "Inactive" => "bg-warning text-dark",
            "Incomplete" => "bg-secondary",
            "Suspended" => "bg-danger",
            _ => "bg-secondary"
        };
    }
}
