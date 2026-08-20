namespace Humans.Onboarding.Models;

/// <summary>
/// View model for the Guest dashboard (profileless accounts).
/// </summary>
internal sealed class GuestDashboardViewModel
{
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Whether a deletion request is pending.</summary>
    public bool IsDeletionPending { get; set; }

    /// <summary>When deletion was requested (for display).</summary>
    public DateTime? DeletionRequestedAt { get; set; }

    /// <summary>When the account is scheduled for deletion (for display).</summary>
    public DateTime? DeletionScheduledFor { get; set; }

    /// <summary>Earliest date the deletion can be processed (event hold).</summary>
    public DateTime? DeletionEligibleAfter { get; set; }
}
