namespace Humans.Web.Models;

public class DashboardViewModel
{
    public string DisplayName { get; set; } = string.Empty;
    public string? ProfilePictureUrl { get; set; }
    public string MembershipStatus { get; set; } = "None";

    // Profile
    public bool HasProfile { get; set; }
    public bool ProfileComplete { get; set; }

    // Consents
    public int PendingConsents { get; set; }
    public int TotalRequiredConsents { get; set; }

    // Applications
    public bool HasPendingApplication { get; set; }
    public string? LatestApplicationStatus { get; set; }
    public DateTime? LatestApplicationDate { get; set; }

    // Quick stats
    public DateTime MemberSince { get; set; }
    public DateTime? LastLogin { get; set; }
}
