using Humans.Domain.Enums;

namespace Humans.Web.Models;

public class OnboardingReviewIndexViewModel
{
    public List<OnboardingReviewItemViewModel> PendingReviews { get; set; } = [];
    public List<OnboardingReviewItemViewModel> FlaggedReviews { get; set; } = [];
}

public class OnboardingReviewItemViewModel
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? ProfilePictureUrl { get; set; }
    public string Email { get; set; } = string.Empty;
    public ConsentCheckStatus ConsentCheckStatus { get; set; }
    public MembershipTier MembershipTier { get; set; }
    public DateTime ProfileCreatedAt { get; set; }
    public bool HasPendingApplication { get; set; }
}

public class OnboardingReviewDetailViewModel
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? ProfilePictureUrl { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? City { get; set; }
    public string? CountryCode { get; set; }
    public MembershipTier MembershipTier { get; set; }
    public ConsentCheckStatus? ConsentCheckStatus { get; set; }
    public string? ConsentCheckNotes { get; set; }
    public DateTime ProfileCreatedAt { get; set; }
    public int ConsentCount { get; set; }
    public int RequiredConsentCount { get; set; }
    public bool HasPendingApplication { get; set; }
    public string? ApplicationMotivation { get; set; }
}
