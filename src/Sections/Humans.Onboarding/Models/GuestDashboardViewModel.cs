namespace Humans.Onboarding.Models;

/// <summary>
/// View model for the Guest dashboard (profileless accounts).
/// </summary>
internal sealed class GuestDashboardViewModel
{
    public string DisplayName { get; set; } = string.Empty;

    public bool IsDeletionPending { get; set; }

    public DateTime? DeletionRequestedAt { get; set; }

    public DateTime? DeletionScheduledFor { get; set; }

    /// <summary>Earliest date the deletion can be processed (event hold).</summary>
    public DateTime? DeletionEligibleAfter { get; set; }
}
