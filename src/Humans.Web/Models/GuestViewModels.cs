namespace Humans.Web.Models;

/// <summary>
/// View model for the Guest dashboard (profileless accounts).
/// </summary>
public class GuestDashboardViewModel
{
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Whether the user has any matched ticket orders or attendee records.</summary>
    public bool HasTickets { get; set; }

    /// <summary>Summary of the user's ticket orders.</summary>
    public List<GuestTicketOrderSummary> TicketOrders { get; set; } = [];

    /// <summary>Whether a deletion request is pending.</summary>
    public bool IsDeletionPending { get; set; }

    /// <summary>When deletion was requested (for display).</summary>
    public DateTime? DeletionRequestedAt { get; set; }

    /// <summary>When the account is scheduled for deletion (for display).</summary>
    public DateTime? DeletionScheduledFor { get; set; }

    /// <summary>Earliest date the deletion can be processed (event hold).</summary>
    public DateTime? DeletionEligibleAfter { get; set; }
}

/// <summary>
/// Summary of a ticket order for the Guest dashboard.
/// </summary>
public class GuestTicketOrderSummary
{
    public string BuyerName { get; set; } = string.Empty;
    public DateTime PurchasedAt { get; set; }
    public int AttendeeCount { get; set; }
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = string.Empty;
}
