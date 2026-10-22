namespace Humans.Onboarding.Models;

internal sealed class GuestDashboardViewModel
{
    public string DisplayName { get; set; } = string.Empty;

    public bool IsDeletionPending { get; set; }

    public DateTime? DeletionRequestedAt { get; set; }

    public DateTime? DeletionScheduledFor { get; set; }

    /// <summary>Earliest date the deletion can be processed (event hold).</summary>
    public DateTime? DeletionEligibleAfter { get; set; }
}
