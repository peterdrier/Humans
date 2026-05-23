using Humans.Domain.Enums;

namespace Humans.Web.Models;

public class DashboardViewModel
{
    public int? EventYear { get; set; }
    public ParticipationStatus? ParticipationStatus { get; set; }

    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? ProfilePictureUrl { get; set; }
    public MembershipStatus MembershipStatus { get; set; }

    public bool HasProfile { get; set; }
    public bool ProfileComplete { get; set; }
    public int ProfileCompletionPercent { get; init; }

    public int PendingConsents { get; set; }
    public int TotalRequiredConsents { get; set; }

    public bool IsVolunteerMember { get; set; }
    public MembershipTier MembershipTier { get; set; }
    public ConsentCheckStatus? ConsentCheckStatus { get; set; }

    public bool IsRejected { get; set; }
    public string? RejectionReason { get; set; }

    public bool HasPendingApplication { get; set; }
    public ApplicationStatus? LatestApplicationStatus { get; set; }
    public DateTime? LatestApplicationDate { get; set; }
    public MembershipTier? LatestApplicationTier { get; set; }

    // Term status (Colaborador/Asociado)
    public DateTime? TermExpiresAt { get; set; }
    public bool TermExpiresSoon { get; set; }
    public bool TermExpired { get; set; }

    public bool IsShiftBrowsingOpen { get; set; }
    public string? EventName { get; set; }

    public DateTime MemberSince { get; set; }
    public DateTime? LastLogin { get; set; }

    public bool HasShiftSignups { get; set; }

    public bool TicketsConfigured { get; set; }
    public bool HasTicket { get; set; }
    public int UserTicketCount { get; set; }
    public string? TicketPurchaseUrl { get; set; }

    /// <summary>Attendees on the user's orders (rendered when count > 1).</summary>
    public IReadOnlyList<MyAttendeeRowVm> MyAttendees { get; set; } = [];

    /// <summary>How many transfer requests this user has currently in Pending state.</summary>
    public int PendingTransferOutCount { get; set; }
}

public sealed record MyAttendeeRowVm(
    Guid AttendeeId,
    string AttendeeName,
    string? AttendeeEmail,
    string VendorTicketId,
    string TicketTypeName,
    Humans.Domain.Enums.TicketAttendeeStatus Status,
    bool CanSendTransfer,
    bool HasPendingOutgoingTransfer,
    Guid? PendingTransferRequestId);
