using Humans.Consent.Contracts;
using Humans.Governance.Contracts;

namespace Humans.Consent.Models;

internal sealed class ConsentReviewQueueIndexViewModel
{
    public List<ConsentReviewQueueItemViewModel> PendingReviews { get; set; } = [];
    public List<ConsentReviewQueueItemViewModel> FlaggedReviews { get; set; } = [];
}

internal sealed class ConsentReviewQueueItemViewModel
{
    public Guid UserId { get; set; }
    public string LegalName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public ConsentCheckStatus? ConsentCheckStatus { get; set; }
    public MembershipTier MembershipTier { get; set; }
    public DateTime ProfileCreatedAt { get; set; }
    public bool HasPendingApplication { get; set; }
    public int ConsentCount { get; set; }
    public int RequiredConsentCount { get; set; }
}

internal sealed class ConsentReviewQueueDetailViewModel
{
    public Guid UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public MembershipTier MembershipTier { get; set; }
    public ConsentCheckStatus? ConsentCheckStatus { get; set; }
    public string? ConsentCheckNotes { get; set; }
    public DateTime ProfileCreatedAt { get; set; }
    public int ConsentCount { get; set; }
    public int RequiredConsentCount { get; set; }
    public bool HasPendingApplication { get; set; }
    public string? ApplicationMotivation { get; set; }
}
