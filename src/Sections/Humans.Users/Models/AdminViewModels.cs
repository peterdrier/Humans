using Humans.Campaigns.Contracts;
using Humans.UI.Models;
using Humans.Governance.Contracts;
using Humans.Users.Contracts;

namespace Humans.Users.Models;

internal sealed class AdminHumanListViewModel : PagedListViewModel
{
    /// <summary>
    /// Page of admin humans to render via the canonical
    /// <c>_HumanSearchResults</c> partial. Admin-specific fields
    /// (<c>AdminEmail</c>, <c>MembershipStatus</c>, <c>CreatedAt</c>,
    /// <c>LastLoginAt</c>, <c>AdminDetailUrl</c>) are pre-populated by the
    /// controller so the partial can render them inline.
    /// </summary>
    public List<HumanSearchResultViewModel> Humans { get; set; } = [];
    public string? SearchTerm { get; set; }
    public string? StatusFilter { get; set; }
    public string SortBy { get; set; } = "name";
    public string SortDir { get; set; } = "asc";
}


internal sealed class AdminHumanDetailViewModel
{
    public Guid UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? ProfilePictureUrl { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }

    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? City { get; set; }
    public string? CountryCode { get; set; }
    public UserState State { get; set; }

    /// <summary>This account is a merge tombstone (folded into another and locked out).</summary>
    public bool IsMerged { get; set; }
    public Guid? MergedToUserId { get; set; }
    public DateTime? MergedAt { get; set; }
    public string? MergedToDisplayName { get; set; }
    public string? AdminNotes { get; set; }
    public MembershipTier MembershipTier { get; set; }
    public ConsentCheckStatus? ConsentCheckStatus { get; set; }
    public string? EmergencyContactName { get; set; }
    public string? EmergencyContactPhone { get; set; }
    public string? EmergencyContactRelationship { get; set; }
    public string? PreferredLanguage { get; set; }

    public string? RejectionReason { get; set; }
    public DateTime? RejectedAt { get; set; }
    public string? RejectedByName { get; set; }

    public string? NobodiesTeamEmail { get; set; }

    public string? OAuthEmail { get; set; }
    public string? GoogleServiceEmail { get; set; }
    public GoogleEmailStatus GoogleEmailStatus { get; set; }
    public List<AdminUserEmailViewModel> UserEmails { get; set; } = [];

    public int ApplicationCount { get; set; }
    public int ConsentCount { get; set; }
    public IReadOnlyList<CampaignGrantSummary> CampaignGrants { get; set; } = [];
    public int OutboxCount { get; set; }
    public List<AdminHumanApplicationViewModel> Applications { get; set; } = [];
    public List<AdminRoleAssignmentViewModel> RoleAssignments { get; set; } = [];
    public IReadOnlyList<ProfileLanguageDisplayViewModel> Languages { get; set; } = [];

    public string? MaskedIban { get; set; }
    /// <summary>
    /// Set by the RevealIban action via TempData. Survives exactly one page load after reveal.
    /// </summary>
    public string? RevealedIban { get; set; }
}

internal sealed class AdminUserEmailViewModel
{
    public string Email { get; set; } = string.Empty;
    public bool IsGoogle { get; set; }
    public bool IsVerified { get; set; }
    public bool IsPrimary { get; set; }
    public ContactFieldVisibility? Visibility { get; set; }
}

internal sealed class AdminHumanApplicationViewModel
{
    public Guid Id { get; set; }
    public ApplicationStatus Status { get; set; }
    public DateTime SubmittedAt { get; set; }
}

internal sealed class AdminRoleAssignmentListViewModel() : PagedListViewModel(50)
{
    public List<AdminRoleAssignmentViewModel> RoleAssignments { get; set; } = [];
    public string? RoleFilter { get; set; }
    public bool ShowInactive { get; set; }
}

internal sealed class AdminRoleAssignmentViewModel
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
    public DateTime ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; }
    public string? CreatedByName { get; set; }
    public DateTime CreatedAt { get; set; }
}

internal sealed class CreateRoleAssignmentViewModel
{
    public Guid UserId { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public List<string> AvailableRoles { get; set; } = [];
}

internal sealed class EndRoleAssignmentViewModel
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public string? Notes { get; set; }
}

internal sealed class AccountMergeQueueViewModel
{
    public List<AccountMergeRowViewModel> Rows { get; set; } = [];
}

internal sealed class AccountMergeRowViewModel
{
    public Guid? RequestId { get; set; }
    public string SharedEmail { get; set; } = string.Empty;
    public ProfileSummaryViewModel AccountA { get; set; } = new();
    public ProfileSummaryViewModel AccountB { get; set; } = new();
    public bool FromUserRequest { get; set; }
    public bool AlreadyMerged { get; set; }
    public DateTime? RequestedAt { get; set; }
}

/// <summary>
/// Audience segmentation gauges for admin view.
/// Shows total accounts, accounts with tickets, with profiles, both, or neither.
/// </summary>
internal sealed class AudienceSegmentationViewModel
{
    public int TotalAccounts { get; set; }
    public int WithTicket { get; set; }
    public int WithProfile { get; set; }
    public int WithBoth { get; set; }
    public int WithNeither { get; set; }

    /// <summary>Available event years for filtering (e.g. 2025, 2026).</summary>
    public List<int> AvailableYears { get; set; } = [];

    /// <summary>Currently selected event year filter, or null for all time.</summary>
    public int? SelectedYear { get; set; }
}
