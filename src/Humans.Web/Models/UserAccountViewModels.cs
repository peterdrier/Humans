using Humans.Domain.Enums;

namespace Humans.Web.Models;

/// <summary>The account-status wall shown to Suspended/AdminSuspended/Rejected/Deleted/Merged users.</summary>
public sealed class AccountStatusViewModel
{
    public required UserState State { get; init; }
    public required Guid UserId { get; init; }
    public required string ContactEmail { get; init; }

    /// <summary>Shown only for Rejected, when a reason was recorded.</summary>
    public string? RejectionReason { get; init; }
}

/// <summary>The single screen a DeletePending user can reach — cancel the pending deletion.</summary>
public sealed class PendingDeletionViewModel
{
    public DateTime? ScheduledFor { get; init; }
}
