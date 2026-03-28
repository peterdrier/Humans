using Humans.Domain.Enums;

namespace Humans.Web.Models;

public class DashboardViewModel
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? ProfilePictureUrl { get; set; }
    public MembershipStatus MembershipStatus { get; set; }

    // Profile
    public bool HasProfile { get; set; }
    public bool ProfileComplete { get; set; }

    // Consents
    public int PendingConsents { get; set; }
    public int TotalRequiredConsents { get; set; }

    // Membership
    public bool IsVolunteerMember { get; set; }
    public MembershipTier MembershipTier { get; set; }
    public ConsentCheckStatus? ConsentCheckStatus { get; set; }

    // Rejection
    public bool IsRejected { get; set; }
    public string? RejectionReason { get; set; }

    // Applications
    public bool HasPendingApplication { get; set; }
    public ApplicationStatus? LatestApplicationStatus { get; set; }
    public DateTime? LatestApplicationDate { get; set; }
    public MembershipTier? LatestApplicationTier { get; set; }

    // Term status (Colaborador/Asociado)
    public DateTime? TermExpiresAt { get; set; }
    public bool TermExpiresSoon { get; set; }
    public bool TermExpired { get; set; }

    // Shift discovery
    public bool IsShiftBrowsingOpen { get; set; }
    public string? EventName { get; set; }

    // Quick stats
    public DateTime MemberSince { get; set; }
    public DateTime? LastLogin { get; set; }
}
