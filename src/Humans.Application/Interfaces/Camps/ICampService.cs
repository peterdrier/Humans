using Humans.Application.DTOs;
using Humans.Application.Services.Camps;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using NodaTime;

namespace Humans.Application.Interfaces.Camps;

/// <summary>
/// Service for managing camps and camp-season state.
/// </summary>
public interface ICampService : ICampServiceRead, IApplicationService
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
    Task<CampEditData?> GetCampEditDataAsync(
        Guid campId,
        int? preferredYear = null,
        CancellationToken cancellationToken = default);

    // Season management
    Task<CampSeason> OptInToSeasonAsync(Guid campId, int year, CancellationToken cancellationToken = default);
    Task UpdateSeasonAsync(Guid seasonId, CampSeasonData data, CancellationToken cancellationToken = default);
    Task ApproveSeasonAsync(Guid seasonId, Guid reviewedByUserId, string? notes, CancellationToken cancellationToken = default);
    Task RejectSeasonAsync(Guid seasonId, Guid reviewedByUserId, string notes, CancellationToken cancellationToken = default);
    Task WithdrawSeasonAsync(Guid seasonId, CancellationToken cancellationToken = default);
    Task ReactivateSeasonAsync(Guid seasonId, CancellationToken cancellationToken = default);
    // Camp updates
    Task<CampUpdateResult> UpdateCampAsync(CampUpdateInput input, CancellationToken cancellationToken = default);
    Task DeleteCampAsync(Guid campId, CancellationToken cancellationToken = default);

    // Historical names
    Task AddHistoricalNameAsync(Guid campId, string name, CancellationToken cancellationToken = default);
    Task RemoveHistoricalNameAsync(Guid historicalNameId, CancellationToken cancellationToken = default);

    // Images
    Task<CampImageUploadResult> UploadImageAsync(Guid campId, Stream fileStream, string fileName, string contentType, long length, CancellationToken cancellationToken = default);
    Task DeleteImageAsync(Guid imageId, CancellationToken cancellationToken = default);
    Task ReorderImagesAsync(Guid campId, List<Guid> imageIdsInOrder, CancellationToken cancellationToken = default);

    // Settings (CampAdmin)
    Task SetPublicYearAsync(int year, CancellationToken cancellationToken = default);
    Task OpenSeasonAsync(int year, CancellationToken cancellationToken = default);
    Task CloseSeasonAsync(int year, CancellationToken cancellationToken = default);
    Task SetNameLockDateAsync(int year, LocalDate lockDate, CancellationToken cancellationToken = default);

    // Name change (handles historical name logging)
    Task ChangeSeasonNameAsync(Guid seasonId, string newName, CancellationToken cancellationToken = default);

    // ==========================================================================
    // Camp membership per season (issue nobodies-collective#488)
    // ==========================================================================

    /// <summary>Idempotent — returns existing row's id with an <c>Already*</c> outcome if one exists.</summary>
    Task<CampMemberRequestResult> RequestCampMembershipAsync(
        Guid campId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Throws if the membership's season belongs to a different camp than <paramref name="scopedCampId"/>.</summary>
    Task ApproveCampMemberAsync(
        Guid scopedCampId, Guid campMemberId, Guid approvedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Throws if the membership's season belongs to a different camp than <paramref name="scopedCampId"/>.</summary>
    Task RejectCampMemberAsync(
        Guid scopedCampId, Guid campMemberId, Guid rejectedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Throws if the membership's season belongs to a different camp than <paramref name="scopedCampId"/>.</summary>
    Task RemoveCampMemberAsync(
        Guid scopedCampId, Guid campMemberId, Guid removedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Bypasses the request/approve flow for the camp's active season. Idempotent. Caller authorizes.</summary>
    Task<AddCampMemberOutcome> AddCampMemberToActiveSeasonAsync(
        Guid campId, Guid userId, Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds the human as an active member of the season (idempotent — no-op if
    /// already active) and then assigns them the given camp role in a single
    /// operation. Used by the camp-edit role picker so callers don't have to
    /// orchestrate the two sub-mutations themselves. Caller authorizes.
    /// </summary>
    Task<AssignCampRoleOutcome> AddMemberAndAssignRoleInActiveSeasonAsync(
        Guid campId, Guid roleDefinitionId, Guid userId, Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Throws if <paramref name="userId"/> is not the row's owner.</summary>
    Task WithdrawCampMembershipRequestAsync(
        Guid campMemberId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Returns failure if <paramref name="userId"/> is not the row's owner or the row cannot be left.</summary>
    Task<CampMembershipMutationResult> LeaveCampAsync(
        Guid campMemberId, Guid userId, CancellationToken cancellationToken = default);

    // ==========================================================================
    // Early Entry (issue nobodies-collective#490)
    // ==========================================================================

    /// <summary>
    /// Sets the global Early Entry start date in CampSettings. CampAdmin/Admin only;
    /// authorization enforced at the controller layer.
    /// </summary>
    Task SetEeStartDateAsync(
        LocalDate? eeStartDate, Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the EE slot cap for a given camp season. CampAdmin/Admin only.
    /// Allowed to drop below the current granted-count: existing grants are retained
    /// but no new grants can be issued until the granted-count falls back under the cap.
    /// </summary>
    Task SetCampSeasonEeSlotCountAsync(
        Guid campSeasonId, int slotCount, Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Grants or revokes Early Entry for a CampMember. Camp lead, CoLead, CampAdmin,
    /// or Admin only; authorization enforced at the controller layer.
    /// <paramref name="scopedCampId"/> must match the camp the member belongs to;
    /// returns MemberNotFound when the member does not exist or belongs to a different camp.
    /// Rejects when granting would push the season's active-granted count above
    /// CampSeason.EeSlotCount, or when the member is not Status=Active.
    /// Idempotent: writes no audit row when the value is already at the requested state.
    /// </summary>
    Task<SetEarlyEntryOutcome> SetEarlyEntryAsync(
        Guid scopedCampId, Guid campMemberId, bool granted, Guid actorUserId,
        CancellationToken cancellationToken = default);
}

public sealed record CampMemberLookup(Guid CampSeasonId, Guid UserId, CampMemberStatus Status);

public sealed record CampSettingsInfo(
    int PublicYear,
    IReadOnlyList<int> OpenSeasons,
    LocalDate? EeStartDate)
{
    public IReadOnlyDictionary<int, LocalDate?> NameLockDates { get; init; } =
        new Dictionary<int, LocalDate?>();
}

/// <summary>
/// Canonical Camps read-model entry (T-06). One <see cref="CampInfo"/> per
/// camp in the <c>CachingCampService</c> projection; year-keyed views like
/// <see cref="ICampService.GetCampsForYearAsync"/> are filtered snapshots of
/// this canonical per-camp cache, never separate cache entries.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cache size budget.</b> At Nobodies' steady-state (~100 active camps,
/// ≤5 leads/camp, ≤5 historical names/camp) the per-entry footprint is
/// dominated by season blurbs (~2 KB/season) plus scalar fields. Warmup
/// loads only the seasons referenced by <c>CampSettings.PublicYear</c> +
/// <c>OpenSeasons</c> + the current real-world year — typically 1–3 seasons
/// per camp, not full history. Worst-case ~50 KB/camp × 100 camps ≈ 5 MB —
/// comfortably under the ~50 MB §15 budget for a 500-user-scale projection.
/// </para>
/// <para>
/// <b>EeGrantedCount cross-table invariant.</b>
/// <see cref="CampSeasonInfo.EeGrantedCount"/> is computed from
/// <c>camp_members.HasEarlyEntry</c> WHERE <c>Status = Active</c>. The
/// methods that flip those fields —
/// <see cref="ICampService.SetEarlyEntryAsync"/>,
/// <see cref="ICampService.RemoveCampMemberAsync"/>,
/// <see cref="ICampService.LeaveCampMembershipAsync"/>, and the membership
/// confirm/withdraw paths — invalidate the affected camp via
/// <see cref="ICampInfoInvalidator"/> inside the decorator, so the cached
/// projection rebuilds with the current count on the next read. No bypass
/// is possible because only the inner <c>CampService</c> /
/// <c>CampRoleService</c> may touch <c>ICampRepository</c>
/// (pinned by <c>CampsArchitectureTests</c>).
/// </para>
/// </remarks>
public sealed record CampInfo(
    Guid Id,
    string Slug,
    string ContactEmail,
    string ContactPhone,
    bool IsSwissCamp,
    int TimesAtNowhere,
    IReadOnlyList<CampSeasonInfo> Seasons)
{
    public string? WebOrSocialUrl { get; init; }

    public IReadOnlyList<CampLink>? Links { get; init; }

    public bool HideHistoricalNames { get; init; }

    public IReadOnlyList<string> HistoricalNames { get; init; } = [];

    public IReadOnlyList<CampImageSummary> Images { get; init; } = [];

    /// <summary>
    /// The latest season by year. Derived from <see cref="Seasons"/>; not a constructor parameter.
    /// </summary>
    public CampSeasonInfo? Active => Seasons.OrderByDescending(s => s.Year).FirstOrDefault();

    public CampSeasonInfo? CurrentSeason => Active;

    public CampSeasonInfo? GetSeasonForYear(int year, bool fallbackToLatestSeason = false)
    {
        var season = Seasons
            .Where(s => s.Year == year)
            .OrderByDescending(s => s.Year)
            .FirstOrDefault();

        return season ?? (fallbackToLatestSeason ? Active : null);
    }

    public CampMembershipState GetMembershipState(Guid userId)
    {
        var season = Seasons
            .Where(s => s.Status is CampSeasonStatus.Active or CampSeasonStatus.Full)
            .OrderByDescending(s => s.Year)
            .FirstOrDefault();
        if (season is null)
        {
            return new CampMembershipState(null, null, null, CampMemberStatusSummary.NoOpenSeason);
        }

        return season.GetMembershipState(userId);
    }

    public bool IsLead(Guid userId) => Seasons.Any(season => season.IsLead(userId));

    public bool IsEventManager(Guid userId) => Seasons.Any(season => season.IsEventManager(userId));

    public Guid? GetLeadSeasonIdForYear(Guid userId, int year) =>
        Seasons
            .Where(season => season.Year == year && season.IsLead(userId))
            .OrderBy(season => season.Id)
            .Select(season => (Guid?)season.Id)
            .FirstOrDefault();
}

public sealed record CampSeasonInfo(
    Guid Id,
    Guid CampId,
    string CampSlug,
    int Year,
    LocalDate? NameLockDate,
    string Name,
    string BlurbShort,
    string Languages,
    IReadOnlyList<CampVibe> Vibes,
    CampSeasonStatus Status,
    YesNoMaybe AcceptingMembers,
    YesNoMaybe KidsWelcome,
    AdultPlayspacePolicy AdultPlayspace,
    int MemberCount,
    SoundZone? SoundZone,
    SpaceSize? SpaceRequirement,
    ElectricalGrid? ElectricalGrid,
    int EeSlotCount,
    int? EeGrantedCount,
    int? JoinedMemberCount)
{
    public string BlurbLong { get; init; } = string.Empty;

    public KidsVisitingPolicy KidsVisiting { get; init; }

    public string? KidsAreaDescription { get; init; }

    public PerformanceSpaceStatus HasPerformanceSpace { get; init; }

    public string? PerformanceTypes { get; init; }

    public IReadOnlyList<CampSeasonMemberInfo> Members { get; init; } = [];

    public IReadOnlyList<Guid> LeadUserIds { get; init; } = [];

    public IReadOnlyList<Guid> WorkshopLeadUserIds { get; init; } = [];

    public IReadOnlyList<CampSeasonMemberInfo> ActiveMembers =>
        Members.Where(member => member.Status == CampMemberStatus.Active).ToList();

    public IReadOnlyList<CampSeasonMemberInfo> PendingMembers =>
        Members.Where(member => member.Status == CampMemberStatus.Pending).ToList();

    public CampMembershipState GetMembershipState(Guid userId)
    {
        var member = Members.FirstOrDefault(m => m.UserId == userId);
        if (member is null)
        {
            return new CampMembershipState(Year, Id, null, CampMemberStatusSummary.None);
        }

        var status = member.Status == CampMemberStatus.Active
            ? CampMemberStatusSummary.Active
            : CampMemberStatusSummary.Pending;

        return new CampMembershipState(Year, Id, member.Id, status);
    }

    public bool IsLead(Guid userId) => LeadUserIds.Contains(userId);

    public bool IsEventManager(Guid userId) =>
        LeadUserIds.Contains(userId) || WorkshopLeadUserIds.Contains(userId);

    public bool IsNameLocked(LocalDate today) =>
        NameLockDate.HasValue && today >= NameLockDate.Value;
}

public sealed record CampSeasonMemberInfo(
    Guid Id,
    Guid UserId,
    CampMemberStatus Status,
    Instant RequestedAt,
    Instant? ConfirmedAt,
    bool HasEarlyEntry);

/// <summary>
/// Result of a camp membership request action.
/// </summary>
public record CampMemberRequestResult(
    Guid CampMemberId,
    CampMemberRequestOutcome Outcome,
    string Message,
    CampMemberRequestNoticeLevel NoticeLevel);

public sealed record CampMembershipMutationResult(bool Succeeded, string? ErrorMessage)
{
    public static CampMembershipMutationResult Success() => new(true, null);

    public static CampMembershipMutationResult Failure(string errorMessage) => new(false, errorMessage);
}

public sealed record CampUpdateInput(
    Guid CampId,
    string ContactEmail,
    string ContactPhone,
    string? WebOrSocialUrl,
    List<CampLink>? Links,
    bool IsSwissCamp,
    int TimesAtNowhere,
    bool HideHistoricalNames,
    Guid SeasonId,
    string SeasonName,
    CampSeasonData SeasonData);

public sealed record CampUpdateResult(bool Succeeded, string? ErrorMessage)
{
    public static CampUpdateResult Success() => new(true, null);

    public static CampUpdateResult Failure(string errorMessage) => new(false, errorMessage);
}

public enum CampMemberRequestOutcome
{
    /// <summary>A new pending request was created.</summary>
    Created,
    /// <summary>An existing pending request already existed for the human.</summary>
    AlreadyPending,
    /// <summary>The human is already an active member of the camp for this season.</summary>
    AlreadyActive,
    /// <summary>No open season for the camp — the request was not created.</summary>
    NoOpenSeason
}

public enum CampMemberRequestNoticeLevel
{
    Success,
    Info,
    Error
}

public enum AddCampMemberOutcome
{
    Added,
    InvalidUser,
    NoActiveSeason
}

/// <summary>
/// Current human's camp membership state relative to a camp's open-season.
/// </summary>
public record CampMembershipState(
    int? OpenSeasonYear,
    Guid? OpenSeasonId,
    Guid? CampMemberId,
    CampMemberStatusSummary Status);

public enum CampMemberStatusSummary
{
    /// <summary>No open season for the camp.</summary>
    NoOpenSeason,
    /// <summary>Open season exists but human has no record.</summary>
    None,
    /// <summary>Human has a pending request.</summary>
    Pending,
    /// <summary>Human is an active member.</summary>
    Active
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
    ElectricalGrid? ElectricalGrid);

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
    ElectricalGrid? ElectricalGrid,
    IReadOnlyList<CampImageSummary> Images,
    IReadOnlyList<CampHistoricalNameSummary> HistoricalNames);

public record CampImageSummary(
    Guid Id,
    string Url,
    int SortOrder);

public sealed record CampImageUploadResult(bool Succeeded, CampImage? Image, string? ErrorMessage)
{
    public static CampImageUploadResult Success(CampImage image) => new(true, image, null);

    public static CampImageUploadResult Failure(string errorMessage) => new(false, null, errorMessage);
}

public record CampHistoricalNameSummary(
    Guid Id,
    string Name,
    int? Year,
    string Source);

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
    string Status,
    string? ElectricalGrid);

public record CampSeasonBrief(Guid CampSeasonId, string Name, string CampSlug, SpaceSize? SpaceRequirement);
