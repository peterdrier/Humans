using System.ComponentModel.DataAnnotations;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;

namespace Humans.Web.Models;

// Public listing
public class CampIndexViewModel
{
    public int Year { get; set; }
    public List<CampCardViewModel> Camps { get; set; } = new();
    public List<CampCardViewModel> MyCamps { get; set; } = new();
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
    public List<CampVibe> Vibes { get; set; } = new();
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
}

// Detail page
public class CampDetailViewModel
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<CampLink> Links { get; set; } = new();
    public bool IsSwissCamp { get; set; }
    public int TimesAtNowhere { get; set; }
    public List<string> HistoricalNames { get; set; } = new();
    public List<string> ImageUrls { get; set; } = new();
    public List<CampLeadViewModel> Leads { get; set; } = new();
    public CampSeasonDetailViewModel? CurrentSeason { get; set; }
    public bool IsCurrentUserLead { get; set; }
    public bool IsCurrentUserCampAdmin { get; set; }
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
    public List<CampVibe> Vibes { get; set; } = new();
    public AdultPlayspacePolicy AdultPlayspace { get; set; }
    public int MemberCount { get; set; }
    public SpaceSize? SpaceRequirement { get; set; }
    public SoundZone? SoundZone { get; set; }
    public int ContainerCount { get; set; }
    public string? ContainerNotes { get; set; }
    public ElectricalGrid? ElectricalGrid { get; set; }
    public bool IsNameLocked { get; set; }
}

public class CampLeadViewModel
{
    public Guid LeadId { get; set; }
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

// Registration form
public class CampRegisterViewModel
{
    public string Name { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
    public string ContactPhone { get; set; } = string.Empty;
    public List<string> Links { get; set; } = new();
    public bool IsSwissCamp { get; set; }
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
    public List<CampVibe> Vibes { get; set; } = new();
    public AdultPlayspacePolicy AdultPlayspace { get; set; }
    public int MemberCount { get; set; }
    public SpaceSize? SpaceRequirement { get; set; }
    public SoundZone? SoundZone { get; set; }
    public int ContainerCount { get; set; }
    public string? ContainerNotes { get; set; }
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
    public List<CampLeadViewModel> Leads { get; set; } = new();
    public List<CampImageViewModel> Images { get; set; } = new();
}

public class CampImageViewModel
{
    public Guid Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public int SortOrder { get; set; }
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
    public List<CampCardViewModel> PendingCamps { get; set; } = new();
    public int PublicYear { get; set; }
    public List<int> OpenSeasons { get; set; } = new();
    public int TotalCamps { get; set; }
    public int ActiveCamps { get; set; }
    public Dictionary<int, NodaTime.LocalDate?> NameLockDates { get; set; } = new();
}
