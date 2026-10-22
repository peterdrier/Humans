using Humans.Base.Enums;
using NodaTime;

namespace Humans.Camps.Contracts;

/// <summary><see cref="PublicYear"/> is Settings' active event year, else the clock year.</summary>
public sealed record CampSettingsInfo(
    int PublicYear,
    IReadOnlyList<int> OpenSeasons)
{
    public IReadOnlyDictionary<int, LocalDate?> NameLockDates { get; init; } =
        new Dictionary<int, LocalDate?>();
}

/// <summary>
/// One per camp in the cached projection; year-keyed reads filter it rather than caching separately.
/// </summary>
/// <remarks>
/// <see cref="CampSeasonInfo.EeGrantedCount"/> counts Active rows only, for display. The slot-cap check
/// also counts consumed grants kept on Removed rows, so the two numbers differ by design.
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

    /// <summary>The latest season by year, whatever its status.</summary>
    public CampSeasonInfo? Active => Seasons.OrderByDescending(s => s.Year).FirstOrDefault();

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
            .Where(s => s.IsPublic)
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

    /// <summary>Listed in the directory and open for camp management. Full is informational only.</summary>
    public bool IsPublic => Status is CampSeasonStatus.Active or CampSeasonStatus.Full;

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
    bool HasEarlyEntry)
{
    /// <summary>Ordered by role sort order.</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];
}

/// <summary>
/// The active (<c>PublicYear</c>) season only. <see cref="Season"/> is null and <see cref="Roles"/> empty when
/// the user is not an Active member of any camp this year.
/// </summary>
public sealed record CampUserInfo(
    CampSeasonInfo? Season,
    IReadOnlyList<string> Roles)
{
    public static readonly CampUserInfo None = new(null, []);

    /// <summary>
    /// Only the season for <paramref name="activeYear"/> (never a camp's latest) and only Active membership.
    /// First match wins if the user is somehow active in more than one camp.
    /// </summary>
    public static CampUserInfo Resolve(IEnumerable<CampInfo> camps, int activeYear, Guid userId)
    {
        foreach (var camp in camps)
        {
            var season = camp.Seasons.FirstOrDefault(s => s.Year == activeYear);
            var member = season?.Members.FirstOrDefault(
                m => m.UserId == userId && m.Status == CampMemberStatus.Active);
            if (member is not null)
            {
                return new CampUserInfo(season, member.Roles);
            }
        }

        return None;
    }
}

public record CampMembershipState(
    int? OpenSeasonYear,
    Guid? OpenSeasonId,
    Guid? CampMemberId,
    CampMemberStatusSummary Status);

public enum CampMemberStatusSummary
{
    NoOpenSeason,
    /// <summary>The open season has no row for this human.</summary>
    None,
    Pending,
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

public record CampImageSummary(
    Guid Id,
    string Url,
    int SortOrder);
