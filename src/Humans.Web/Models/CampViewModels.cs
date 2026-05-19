using System.ComponentModel.DataAnnotations;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;

namespace Humans.Web.Models;

// Public listing
public class CampIndexViewModel
{
    public int Year { get; set; }
    public List<CampCardViewModel> Camps { get; set; } = [];
    public List<CampCardViewModel> MyCamps { get; set; } = [];
    public CampFilterViewModel Filters { get; set; } = new();
}

public class CampCardViewModel
{
    public Guid Id { get; set; }
    public Guid? SeasonId { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string BlurbShort { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public List<CampVibe> Vibes { get; set; } = [];
    public YesNoMaybe AcceptingMembers { get; set; }
    public YesNoMaybe KidsWelcome { get; set; }
    public SoundZone? SoundZone { get; set; }
    public CampSeasonStatus Status { get; set; }
    public int TimesAtNowhere { get; set; }
}

public class CampFilterViewModel
{
    public CampVibe? Vibe { get; set; }
    public SoundZone? SoundZone { get; set; }
    public bool KidsFriendly { get; set; }
    public bool AcceptingMembers { get; set; }
    public string? Search { get; set; }
}

// Detail page
public class CampDetailViewModel
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<CampLink> Links { get; set; } = [];
    public bool IsSwissCamp { get; set; }
    public bool HideHistoricalNames { get; set; }
    public int TimesAtNowhere { get; set; }
    public List<string> HistoricalNames { get; set; } = [];
    public List<string> ImageUrls { get; set; } = [];
    public List<CampLeadViewModel> Leads { get; set; } = [];
    public CampSeasonDetailViewModel? CurrentSeason { get; set; }
    public bool IsCurrentUserLead { get; set; }
    public bool IsCurrentUserCampAdmin { get; set; }
    public CampMembershipStateViewModel Membership { get; set; } = new();
}

/// <summary>
/// Represents the current authenticated user's relationship to a camp for the open season.
/// Rendered only on authenticated request paths — never displayed to anonymous viewers.
/// </summary>
public class CampMembershipStateViewModel
{
    public int? OpenSeasonYear { get; set; }
    public Guid? CampMemberId { get; set; }
    public CampMemberStatusSummaryView Status { get; set; } = CampMemberStatusSummaryView.NoOpenSeason;
}

public enum CampMemberStatusSummaryView
{
    NoOpenSeason,
    None,
    Pending,
    Active
}

public class CampSeasonDetailViewModel
{
    public Guid Id { get; set; }
    public int Year { get; set; }
    public string Name { get; set; } = string.Empty;
    public CampSeasonStatus Status { get; set; }
    public string BlurbLong { get; set; } = string.Empty;
    public string BlurbShort { get; set; } = string.Empty;
    public string Languages { get; set; } = string.Empty;
    public YesNoMaybe AcceptingMembers { get; set; }
    public YesNoMaybe KidsWelcome { get; set; }
    public KidsVisitingPolicy KidsVisiting { get; set; }
    public string? KidsAreaDescription { get; set; }
    public PerformanceSpaceStatus HasPerformanceSpace { get; set; }
    public string? PerformanceTypes { get; set; }
    public List<CampVibe> Vibes { get; set; } = [];
    public AdultPlayspacePolicy AdultPlayspace { get; set; }
    public int MemberCount { get; set; }
    public SpaceSize? SpaceRequirement { get; set; }
    public SoundZone? SoundZone { get; set; }
    public ElectricalGrid? ElectricalGrid { get; set; }
    public bool IsNameLocked { get; set; }
}

public class CampLeadViewModel
{
    public Guid LeadId { get; set; }
    public Guid UserId { get; set; }
}

// Registration form
public class CampRegisterViewModel
{
    public string Name { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
    public string ContactPhone { get; set; } = string.Empty;
    public List<string> Links { get; set; } = [];
    public bool IsSwissCamp { get; set; }
    public bool HideHistoricalNames { get; set; }
    public int TimesAtNowhere { get; set; }
    public string? HistoricalNames { get; set; }
    public string BlurbLong { get; set; } = string.Empty;
    public string BlurbShort { get; set; } = string.Empty;
    public string Languages { get; set; } = string.Empty;
    public YesNoMaybe AcceptingMembers { get; set; }
    public YesNoMaybe KidsWelcome { get; set; }
    public KidsVisitingPolicy KidsVisiting { get; set; }
    public string? KidsAreaDescription { get; set; }
    public PerformanceSpaceStatus HasPerformanceSpace { get; set; }
    public string? PerformanceTypes { get; set; }
    public List<CampVibe> Vibes { get; set; } = [];
    public AdultPlayspacePolicy AdultPlayspace { get; set; }
    public int MemberCount { get; set; }
    public SpaceSize? SpaceRequirement { get; set; }
    public SoundZone? SoundZone { get; set; }
    public ElectricalGrid? ElectricalGrid { get; set; }
}

// Edit form
public class CampEditViewModel : CampRegisterViewModel
{
    public Guid CampId { get; set; }
    public string Slug { get; set; } = string.Empty;
    public Guid SeasonId { get; set; }
    public int Year { get; set; }
    public bool IsNameLocked { get; set; }
    public List<CampLeadViewModel> Leads { get; set; } = [];
    public List<CampImageViewModel> Images { get; set; } = [];
    public List<CampHistoricalNameViewModel> ExistingHistoricalNames { get; set; } = [];
    public List<CampMemberRowViewModel> PendingMembers { get; set; } = [];
    public List<CampMemberRowViewModel> ActiveMembers { get; set; } = [];
    /// <summary>EE slot cap for the current season (CampAdmin-managed). 0 = no EE.</summary>
    public int EeSlotCount { get; set; }
    /// <summary>Count of active members with HasEarlyEntry=true for the current season.</summary>
    public int EeGrantedCount { get; set; }

    /// <summary>
    /// Per-camp roles panel data (issue nobodies-collective#489). Null when the
    /// camp has no open season — the view hides the panel in that case.
    /// </summary>
    public Camp.CampRolesPanelViewModel? RolesPanel { get; set; }
}

public class CampMemberRowViewModel
{
    public Guid CampMemberId { get; set; }
    public Guid UserId { get; set; }
    public NodaTime.Instant RequestedAt { get; set; }
    public NodaTime.Instant? ConfirmedAt { get; set; }
    public bool IsLead { get; set; }
    public bool HasEarlyEntry { get; set; }
    public CampMemberStatus Status { get; set; }
}

public class CampImageViewModel
{
    public Guid Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}

public class CampHistoricalNameViewModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string Source { get; set; } = string.Empty;
}

// Contact form
public class CampContactViewModel
{
    public string CampSlug { get; set; } = string.Empty;
    public string CampName { get; set; } = string.Empty;

    [Required]
    [StringLength(2000, MinimumLength = 1)]
    public string Message { get; set; } = string.Empty;

    public bool IncludeContactInfo { get; set; } = true;
}

// Admin dashboard
public class CampAdminViewModel
{
    public List<CampCardViewModel> PendingCamps { get; set; } = [];
    public List<CampCardViewModel> WithdrawnCamps { get; set; } = [];
    public int PublicYear { get; set; }
    public List<int> OpenSeasons { get; set; } = [];
    public int TotalCamps { get; set; }
    public int ActiveCamps { get; set; }
    public Dictionary<int, NodaTime.LocalDate?> NameLockDates { get; set; } = new();
    public List<CampSummaryRowViewModel> AllCampSummaries { get; set; } = [];
    public string? RegistrationInfo { get; set; }
    /// <summary>Global EE start date for the public year. Null until set by CampAdmin.</summary>
    public NodaTime.LocalDate? EeStartDate { get; set; }
}

public class CampSummaryRowViewModel
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public Guid? SeasonId { get; set; }
    public string AcceptingMembers { get; set; } = string.Empty;
    public int MemberCount { get; set; }
    public string Zone { get; set; } = string.Empty;
    public string SpaceRequirement { get; set; } = string.Empty;
    public int YearsParticipating { get; set; }
    public List<CampLeadViewModel> Leads { get; set; } = [];
    /// <summary>EE slot cap for this season (CampAdmin-managed).</summary>
    public int EeSlotCount { get; set; }
    /// <summary>Count of Active members with HasEarlyEntry=true for this season.</summary>
    public int EeGrantedCount { get; set; }
    /// <summary>Count of Active <see cref="Humans.Domain.Entities.CampMember"/> rows for this season (humans who joined in-app).</summary>
    public int JoinedMemberCount { get; set; }
}
