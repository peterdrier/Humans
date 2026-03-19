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
    Task<List<Camp>> GetCampsForYearAsync(int year, CancellationToken cancellationToken = default);
    Task<CampSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
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
        CancellationToken cancellationToken = default);
    Task DeleteCampAsync(Guid campId, CancellationToken cancellationToken = default);

    // Lead management
    Task<CampLead> AddLeadAsync(Guid campId, Guid userId, CancellationToken cancellationToken = default);
    Task RemoveLeadAsync(Guid leadId, CancellationToken cancellationToken = default);

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
