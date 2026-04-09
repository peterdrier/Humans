using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using NodaTime;

namespace Humans.Application.Interfaces;

public interface ICampService
{
    // Registration
    Task<Camp> CreateCampAsync(
        Guid createdByUserId,
        string name,
        string contactEmail,
        string contactPhone,
        string? webOrSocialUrl,
        List<CampLink>? links,
        bool isSwissCamp,
        int timesAtNowhere,
        CampSeasonData seasonData,
        List<string>? historicalNames,
        int year,
        CancellationToken cancellationToken = default);

    // Queries
    Task<Camp?> GetCampBySlugAsync(string slug, CancellationToken cancellationToken = default);
    Task<Camp?> GetCampByIdAsync(Guid campId, CancellationToken cancellationToken = default);
    Task<CampDetailData?> GetCampDetailAsync(
        string slug,
        int? preferredYear = null,
        bool fallbackToLatestSeason = true,
        CancellationToken cancellationToken = default);
    Task<CampEditData?> GetCampEditDataAsync(
        Guid campId,
        int? preferredYear = null,
        CancellationToken cancellationToken = default);
    Task<CampDirectoryResult> GetCampDirectoryAsync(
        Guid? userId,
        CampDirectoryFilter? filter = null,
        CancellationToken cancellationToken = default);
    Task<List<Camp>> GetCampsForYearAsync(int year, CancellationToken cancellationToken = default);
    Task<List<Camp>> GetAllCampsForYearAsync(int year, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CampPublicSummary>> GetCampPublicSummariesForYearAsync(int year, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CampPlacementSummary>> GetCampPlacementSummariesForYearAsync(int year, CancellationToken cancellationToken = default);
    Task<CampSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Gets camps with their active leads (and lead user data) for a given year.
    /// Optionally filters to specific season statuses.
    /// </summary>
    Task<List<Camp>> GetCampsWithLeadsForYearAsync(int year, IReadOnlyList<CampSeasonStatus>? statusFilter = null, CancellationToken cancellationToken = default);
    Task<List<Camp>> GetCampsByLeadUserIdAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<List<CampSeason>> GetPendingSeasonsAsync(CancellationToken cancellationToken = default);

    // Season management
    Task<CampSeason> OptInToSeasonAsync(Guid campId, int year, CancellationToken cancellationToken = default);
    Task UpdateSeasonAsync(Guid seasonId, CampSeasonData data, CancellationToken cancellationToken = default);
    Task ApproveSeasonAsync(Guid seasonId, Guid reviewedByUserId, string? notes, CancellationToken cancellationToken = default);
    Task RejectSeasonAsync(Guid seasonId, Guid reviewedByUserId, string notes, CancellationToken cancellationToken = default);
    Task WithdrawSeasonAsync(Guid seasonId, CancellationToken cancellationToken = default);
    Task SetSeasonFullAsync(Guid seasonId, CancellationToken cancellationToken = default);
    Task ReactivateSeasonAsync(Guid seasonId, CancellationToken cancellationToken = default);
    // Camp updates
    Task UpdateCampAsync(Guid campId, string contactEmail, string contactPhone,
        string? webOrSocialUrl, List<CampLink>? links, bool isSwissCamp, int timesAtNowhere,
        bool hideHistoricalNames,
        CancellationToken cancellationToken = default);
    Task DeleteCampAsync(Guid campId, CancellationToken cancellationToken = default);

    // Lead management
    Task<CampLead> AddLeadAsync(Guid campId, Guid userId, CancellationToken cancellationToken = default);
    Task RemoveLeadAsync(Guid leadId, CancellationToken cancellationToken = default);

    // Historical names
    Task AddHistoricalNameAsync(Guid campId, string name, CancellationToken cancellationToken = default);
    Task RemoveHistoricalNameAsync(Guid historicalNameId, CancellationToken cancellationToken = default);

    // Authorization checks
    Task<bool> IsUserCampLeadAsync(Guid userId, Guid campId, CancellationToken cancellationToken = default);

    // Images
    Task<CampImage> UploadImageAsync(Guid campId, Stream fileStream, string fileName, string contentType, long length, CancellationToken cancellationToken = default);
    Task DeleteImageAsync(Guid imageId, CancellationToken cancellationToken = default);
    Task ReorderImagesAsync(Guid campId, List<Guid> imageIdsInOrder, CancellationToken cancellationToken = default);

    // Settings (CampAdmin)
    Task SetPublicYearAsync(int year, CancellationToken cancellationToken = default);
    Task OpenSeasonAsync(int year, CancellationToken cancellationToken = default);
    Task CloseSeasonAsync(int year, CancellationToken cancellationToken = default);
    Task SetNameLockDateAsync(int year, LocalDate lockDate, CancellationToken cancellationToken = default);
    Task<Dictionary<int, LocalDate?>> GetNameLockDatesAsync(List<int> years, CancellationToken cancellationToken = default);

    // Name change (handles historical name logging)
    Task ChangeSeasonNameAsync(Guid seasonId, string newName, CancellationToken cancellationToken = default);
}

public record CampSeasonData(
    string BlurbLong,
    string BlurbShort,
    string Languages,
    YesNoMaybe AcceptingMembers,
    YesNoMaybe KidsWelcome,
    KidsVisitingPolicy KidsVisiting,
    string? KidsAreaDescription,
    PerformanceSpaceStatus HasPerformanceSpace,
    string? PerformanceTypes,
    List<CampVibe> Vibes,
    AdultPlayspacePolicy AdultPlayspace,
    int MemberCount,
    SpaceSize? SpaceRequirement,
    SoundZone? SoundZone,
    int ContainerCount,
    string? ContainerNotes,
    ElectricalGrid? ElectricalGrid);

public record CampDirectoryFilter(
    CampVibe? Vibe = null,
    SoundZone? SoundZone = null,
    bool KidsFriendly = false,
    bool AcceptingMembers = false);

public record CampDirectoryCard(
    Guid Id,
    string Slug,
    string Name,
    string BlurbShort,
    string? ImageUrl,
    IReadOnlyList<CampVibe> Vibes,
    YesNoMaybe AcceptingMembers,
    YesNoMaybe KidsWelcome,
    SoundZone? SoundZone,
    CampSeasonStatus Status,
    int TimesAtNowhere);

public record CampDirectoryResult(
    int Year,
    int PendingCount,
    IReadOnlyList<CampDirectoryCard> Camps,
    IReadOnlyList<CampDirectoryCard> MyCamps);

public record CampDetailData(
    Guid Id,
    string Slug,
    string Name,
    IReadOnlyList<CampLink> Links,
    bool IsSwissCamp,
    int TimesAtNowhere,
    bool HideHistoricalNames,
    IReadOnlyList<string> HistoricalNames,
    IReadOnlyList<string> ImageUrls,
    IReadOnlyList<CampLeadSummary> Leads,
    CampSeasonDetailData? CurrentSeason);

public record CampLeadSummary(
    Guid LeadId,
    Guid UserId,
    string DisplayName);

public record CampEditData(
    Guid CampId,
    string Slug,
    Guid SeasonId,
    int Year,
    bool IsNameLocked,
    string Name,
    string ContactEmail,
    string ContactPhone,
    IReadOnlyList<string> Links,
    bool IsSwissCamp,
    bool HideHistoricalNames,
    int TimesAtNowhere,
    string BlurbLong,
    string BlurbShort,
    string Languages,
    YesNoMaybe AcceptingMembers,
    YesNoMaybe KidsWelcome,
    KidsVisitingPolicy KidsVisiting,
    string? KidsAreaDescription,
    PerformanceSpaceStatus HasPerformanceSpace,
    string? PerformanceTypes,
    IReadOnlyList<CampVibe> Vibes,
    AdultPlayspacePolicy AdultPlayspace,
    int MemberCount,
    SpaceSize? SpaceRequirement,
    SoundZone? SoundZone,
    int ContainerCount,
    string? ContainerNotes,
    ElectricalGrid? ElectricalGrid,
    IReadOnlyList<CampLeadSummary> Leads,
    IReadOnlyList<CampImageSummary> Images,
    IReadOnlyList<CampHistoricalNameSummary> HistoricalNames);

public record CampImageSummary(
    Guid Id,
    string Url,
    int SortOrder);

public record CampHistoricalNameSummary(
    Guid Id,
    string Name,
    int? Year,
    string Source);

public record CampSeasonDetailData(
    Guid Id,
    int Year,
    string Name,
    CampSeasonStatus Status,
    string BlurbLong,
    string BlurbShort,
    string Languages,
    YesNoMaybe AcceptingMembers,
    YesNoMaybe KidsWelcome,
    KidsVisitingPolicy KidsVisiting,
    string? KidsAreaDescription,
    PerformanceSpaceStatus HasPerformanceSpace,
    string? PerformanceTypes,
    IReadOnlyList<CampVibe> Vibes,
    AdultPlayspacePolicy AdultPlayspace,
    int MemberCount,
    SpaceSize? SpaceRequirement,
    SoundZone? SoundZone,
    int ContainerCount,
    string? ContainerNotes,
    ElectricalGrid? ElectricalGrid,
    bool IsNameLocked);

public record CampPublicSummary(
    Guid Id,
    string Slug,
    string Name,
    string BlurbShort,
    string BlurbLong,
    string? ImageUrl,
    IReadOnlyList<string> Vibes,
    string AcceptingMembers,
    string KidsWelcome,
    string? SoundZone,
    string Status,
    int TimesAtNowhere,
    bool IsSwissCamp,
    IReadOnlyList<CampLink>? Links,
    string? WebOrSocialUrl);

public record CampPlacementSummary(
    Guid Id,
    string Slug,
    string Name,
    int MemberCount,
    string? SpaceRequirement,
    string? SoundZone,
    int ContainerCount,
    string? ContainerNotes,
    string Status,
    string? ElectricalGrid);
