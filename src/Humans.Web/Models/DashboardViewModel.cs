namespace Humans.Web.Models;

public class DashboardViewModel
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? ProfilePictureUrl { get; set; }

    public bool HasProfile { get; set; }
    public bool ProfileComplete { get; set; }

    public bool IsRejected { get; set; }
    public string? RejectionReason { get; set; }

    public DateTime MemberSince { get; set; }
    public DateTime? LastLogin { get; set; }
}
