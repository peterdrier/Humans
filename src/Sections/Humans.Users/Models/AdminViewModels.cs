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
    public DateTime CreatedAt { get; set; }
    public DateTime? LastSignInAt { get; set; }

    public UserState State { get; set; }

    public string? AdminNotes { get; set; }
    public MembershipTier MembershipTier { get; set; }
    public string? PreferredLanguage { get; set; }
    public List<AdminUserEmailViewModel> UserEmails { get; set; } = [];

    public int ConsentCount { get; set; }
    public IReadOnlyList<ProfileLanguageDisplayViewModel> Languages { get; set; } = [];
}

internal sealed class AdminUserEmailViewModel
{
    public string Email { get; set; } = string.Empty;
    public bool IsVerified { get; set; }
    public bool IsPrimary { get; set; }
    public ContactFieldVisibility? Visibility { get; set; }
}

internal sealed class AccountMergeQueueViewModel
{
    /// <summary>The pair picked by hand first, then every detected duplicate.</summary>
    public List<AccountMergeRowViewModel> Rows { get; set; } = [];

    /// <summary>Newest first, from the audit log: every merge, by whoever ran it, and every one that faulted.</summary>
    public IReadOnlyList<RecentMergeRow> Recent { get; set; } = [];
}

internal sealed class AccountMergeRowViewModel
{
    /// <summary>Null for a pair picked by hand.</summary>
    public string? SharedEmail { get; set; }
    public ProfileSummaryViewModel AccountA { get; set; } = new();
    public ProfileSummaryViewModel AccountB { get; set; } = new();
    public bool AlreadyMerged { get; set; }
}

/// <summary>One merge from the audit log. <c>By</c> is null when the account holder merged their own two accounts.</summary>
internal sealed record RecentMergeRow(
    NodaTime.Instant When, Guid KeptUserId, Guid MergedAwayUserId, Guid? By, bool Failed, string Details);

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
