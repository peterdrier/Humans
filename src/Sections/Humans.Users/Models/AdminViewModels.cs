using Humans.Base.Models;
using Humans.Governance.Contracts;
using Humans.Users.Contracts;

namespace Humans.Users.Models;

internal sealed class AdminHumanListViewModel : PagedListViewModel
{
    /// <summary>
    /// Unprojected: <c>AdminList</c> renders its own rows, because an admin directory's columns
    /// are not a search result's.
    /// </summary>
    public List<AdminHumanRow> Humans { get; set; } = [];
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

    public string? AdminNotes { get; set; }
    public MembershipTier MembershipTier { get; set; }
    public string? EmergencyContactName { get; set; }
    public string? EmergencyContactPhone { get; set; }
    public string? EmergencyContactRelationship { get; set; }
    public string? PreferredLanguage { get; set; }

    public string? NobodiesTeamEmail { get; set; }

    public string? OAuthEmail { get; set; }
    public List<AdminUserEmailViewModel> UserEmails { get; set; } = [];

    public int ApplicationCount { get; set; }
    public int ConsentCount { get; set; }
    public List<AdminHumanApplicationViewModel> Applications { get; set; } = [];
    public IReadOnlyList<ProfileLanguageDisplayViewModel> Languages { get; set; } = [];
}

internal sealed class AdminUserEmailViewModel
{
    public string Email { get; set; } = string.Empty;
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

/// <summary>Total accounts, with tickets, with profiles, both, or neither.</summary>
internal sealed class AudienceSegmentationViewModel
{
    public int TotalAccounts { get; set; }
    public int WithTicket { get; set; }
    public int WithProfile { get; set; }
    public int WithBoth { get; set; }
    public int WithNeither { get; set; }

    public List<int> AvailableYears { get; set; } = [];

    /// <summary>Null for all time.</summary>
    public int? SelectedYear { get; set; }
}
