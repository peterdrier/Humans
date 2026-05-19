using Humans.Domain.Enums;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Email;

namespace Humans.Web.Models;

public class AdminHumanListViewModel : PagedListViewModel
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


public class AdminHumanDetailViewModel
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
    public bool IsSuspended { get; set; }
    public bool IsApproved { get; set; }
    public bool HasProfile { get; set; }
    public string? AdminNotes { get; set; }
    public MembershipTier MembershipTier { get; set; }
    public ConsentCheckStatus? ConsentCheckStatus { get; set; }
    public string? EmergencyContactName { get; set; }
    public string? EmergencyContactPhone { get; set; }
    public string? EmergencyContactRelationship { get; set; }
    public string? PreferredLanguage { get; set; }

    public bool IsRejected { get; set; }
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

public class AdminUserEmailViewModel
{
    public string Email { get; set; } = string.Empty;
    public bool IsGoogle { get; set; }
    public bool IsVerified { get; set; }
    public bool IsPrimary { get; set; }
    public ContactFieldVisibility? Visibility { get; set; }
}

public class AdminHumanApplicationViewModel
{
    public Guid Id { get; set; }
    public ApplicationStatus Status { get; set; }
    public DateTime SubmittedAt { get; set; }
}

public class AdminApplicationListViewModel : PagedListViewModel
{
    public List<AdminApplicationViewModel> Applications { get; set; } = [];
    public string? StatusFilter { get; set; }
    public string? TierFilter { get; set; }
}

public class AdminApplicationViewModel
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public ApplicationStatus Status { get; set; }
    public string StatusBadgeClass { get; set; } = "bg-secondary";
    public DateTime SubmittedAt { get; set; }
    public string MotivationPreview { get; set; } = string.Empty;
    public MembershipTier MembershipTier { get; set; }
}

public class AdminApplicationDetailViewModel : ApplicationDetailViewModelBase
{
    public Guid UserId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public string? Language { get; set; }
    public bool CanApproveReject { get; set; }
}

public class AdminApplicationActionModel
{
    public Guid ApplicationId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Notes { get; set; }
}

public class AdminRoleAssignmentListViewModel() : PagedListViewModel(50)
{
    public List<AdminRoleAssignmentViewModel> RoleAssignments { get; set; } = [];
    public string? RoleFilter { get; set; }
    public bool ShowInactive { get; set; }
}

public class AdminRoleAssignmentViewModel
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

public class CreateRoleAssignmentViewModel
{
    public Guid UserId { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public List<string> AvailableRoles { get; set; } = [];
}

public class EndRoleAssignmentViewModel
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public string? Notes { get; set; }
}

public class ConfigurationItemViewModel
{
    public string Section { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public bool IsSet { get; set; }
    public string? DisplayValue { get; set; }
    public bool IsSensitive { get; set; }
    public string Importance { get; set; } = "optional";
}

public class AdminConfigurationViewModel
{
    public List<ConfigurationItemViewModel> Items { get; set; } = [];
}

public class EmailPreviewViewModel
{
    public Dictionary<string, List<EmailPreviewItem>> Previews { get; set; } = new(StringComparer.Ordinal);
    public string FromAddress { get; set; } = string.Empty;
}

public class EmailPreviewItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Recipient { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
}

public class AccountMergeListViewModel
{
    public List<AccountMergeRequestViewModel> Requests { get; set; } = [];
}

public class AccountMergeRequestViewModel
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? PrimaryUserEmail { get; set; }
    public Guid PrimaryUserId { get; set; }
    public string? DuplicateUserEmail { get; set; }
    public Guid DuplicateUserId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class AccountMergeDetailViewModel
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public ProfileSummaryViewModel PrimaryUser { get; set; } = new();
    public ProfileSummaryViewModel DuplicateUser { get; set; } = new();
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedByName { get; set; }
    public string? AdminNotes { get; set; }
}

/// <summary>
/// Compact profile summary for inline display ("baseball card").
/// </summary>
public class ProfileSummaryViewModel
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? ProfilePictureUrl { get; set; }
    public string? PreferredLanguage { get; set; }
    public string? MembershipTier { get; set; }
    public string? MembershipStatus { get; set; }
    public DateTime? MemberSince { get; set; }
    public DateTime? LastLogin { get; set; }
    public string? City { get; set; }
    public string? CountryCode { get; set; }
    public bool IsSuspended { get; set; }
    public List<string> Teams { get; set; } = [];

    /// <summary>
    /// Teams the subject belongs to that are flagged <c>IsHidden</c>. Only populated
    /// in the popover render path when the viewer is TeamsAdmin/Board/Admin — kept
    /// separate from <see cref="Teams"/> so the popover can render an admin-only
    /// section below a separator.
    /// </summary>
    public List<string> HiddenTeams { get; set; } = [];

    public IReadOnlyList<ProfileLanguageDisplayViewModel> Languages { get; set; } = [];

    /// <summary>
    /// False when the user exists (AspNetUsers row) but has no Profile row —
    /// e.g. mailing-list / ticketing imports. The popover renders a sparse
    /// "imported account" card in that case instead of 404'ing.
    /// </summary>
    public bool HasProfile { get; set; } = true;
}

public class EmailOutboxViewModel
{
    public int TotalMessageCount { get; set; }
    public int QueuedCount { get; set; }
    public int SentLast24HoursCount { get; set; }
    public int FailedCount { get; set; }
    public bool IsPaused { get; set; }
    public List<EmailOutboxMessageDto> Messages { get; set; } = [];
}

public class DbStatsViewModel
{
    public long TotalQueryCount { get; set; }
    public List<DbStatEntryViewModel> Entries { get; set; } = [];
}

public class DbStatEntryViewModel
{
    public string Operation { get; set; } = string.Empty;
    public string Table { get; set; } = string.Empty;
    public long Count { get; set; }
    public double AverageMs { get; set; }
    public double MaxMs { get; set; }
    public double TotalMs { get; set; }
}

public class CacheStatsViewModel
{
    public long TotalHits { get; set; }
    public long TotalMisses { get; set; }
    public int TotalActiveEntries { get; set; }

    public double OverallHitRatePercent => TotalHits + TotalMisses > 0
        ? Math.Round(TotalHits * 100.0 / (TotalHits + TotalMisses), 1)
        : 0;

    public List<CacheStatEntryViewModel> Entries { get; set; } = [];

    /// <summary>
    /// Stats for the in-memory caching decorators (Profile / User / Team /
    /// ShiftView), rendered in a separate table below the IMemoryCache stats.
    /// These caches have no TTL and track invalidation count instead.
    /// </summary>
    public List<DecoratorCacheStatEntryViewModel> DecoratorEntries { get; set; } = [];
}

public class CacheStatEntryViewModel
{
    public string KeyType { get; set; } = string.Empty;
    public long Hits { get; set; }
    public long Misses { get; set; }
    public double HitRatePercent { get; set; }
    public int ActiveEntries { get; set; }
    public string Ttl { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
}

public class DecoratorCacheStatEntryViewModel
{
    public string Name { get; set; } = string.Empty;
    public int Entries { get; set; }
    public long Hits { get; set; }
    public long Misses { get; set; }
    public long KeyRemovals { get; set; }
    public long BulkInvalidations { get; set; }
    public double HitRatePercent { get; set; }
    public bool IsWarmedUp { get; set; }
}

public class DuplicateAccountListViewModel
{
    public List<DuplicateAccountGroupViewModel> Groups { get; set; } = [];
}

public class DuplicateAccountGroupViewModel
{
    public string SharedEmail { get; set; } = string.Empty;
    public List<DuplicateAccountItemViewModel> Accounts { get; set; } = [];
}

public class DuplicateAccountItemViewModel
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? ProfilePictureUrl { get; set; }
    public string? MembershipTier { get; set; }
    public string? MembershipStatus { get; set; }
    public DateTime? LastLogin { get; set; }
    public DateTime? CreatedAt { get; set; }
    public int TeamCount { get; set; }
    public int RoleAssignmentCount { get; set; }
    public bool HasProfile { get; set; }
    public bool IsProfileComplete { get; set; }
    public List<string> EmailSources { get; set; } = [];
    public List<string> Teams { get; set; } = [];
}

public class DuplicateAccountDetailViewModel
{
    public string SharedEmail { get; set; } = string.Empty;
    public ProfileSummaryViewModel Account1 { get; set; } = new();
    public ProfileSummaryViewModel Account2 { get; set; } = new();
    public List<string> Account1EmailSources { get; set; } = [];
    public List<string> Account2EmailSources { get; set; } = [];
}

/// <summary>
/// Audience segmentation gauges for admin view.
/// Shows total accounts, accounts with tickets, with profiles, both, or neither.
/// </summary>
public class AudienceSegmentationViewModel
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

/// <summary>
/// View model for the <c>/Admin/BackfillUserEmailProviders</c> page. The page
/// is rendered twice — once as a confirmation form (<see cref="HasRun"/> =
/// false) and once after the operator triggers the backfill
/// (<see cref="HasRun"/> = true) showing the results.
/// </summary>
public sealed record BackfillUserEmailProvidersViewModel(
    bool HasRun,
    int UsersProcessed,
    int ProviderRowsUpdated,
    int IsGoogleRowsUpdated,
    int AmbiguousMatchesWarned,
    IReadOnlyList<string> Warnings);
