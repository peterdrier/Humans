using Humans.Settings.Contracts;
using NodaTime;

namespace Humans.Camps.Services;

internal sealed class CampReadProjection(
    ICampRepository repo,
    ISettingsService settingsService,
    IClock clock) : IApplicationService
{
    /// <summary>
    /// Special-role user-id lists (Lead/Workshop) keyed by season+role, plus the full
    /// named-role list per camp member — both projected from a single active-assignment
    /// fetch (active definitions, active members).
    /// </summary>
    internal sealed record CampRoleProjection(
        IReadOnlyDictionary<(Guid CampSeasonId, CampSpecialRole Role), IReadOnlyList<Guid>> SpecialRoleUserIds,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> MemberRoleNames)
    {
        public static readonly CampRoleProjection Empty = new(
            new Dictionary<(Guid, CampSpecialRole), IReadOnlyList<Guid>>(),
            new Dictionary<Guid, IReadOnlyList<string>>());
    }

    internal async Task<CampRoleProjection> GetRoleProjectionForYearsAsync(
        IReadOnlyCollection<int> years, CancellationToken cancellationToken)
    {
        if (years.Count == 0) return CampRoleProjection.Empty;

        var assignments = await repo.GetActiveAssignmentsForYearsAsync(years, cancellationToken);

        var specialRoleUserIds = assignments
            .Where(a => a.Definition.SpecialRole is CampSpecialRole.Lead or CampSpecialRole.Workshop)
            .GroupBy(a => (a.CampSeasonId, a.Definition.SpecialRole))
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<Guid>)group
                    .Select(a => a.CampMember.UserId)
                    .Distinct()
                    .ToList());

        var memberRoleNames = assignments
            .GroupBy(a => a.CampMemberId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group
                    .OrderBy(a => a.Definition.SortOrder)
                    .ThenBy(a => a.Definition.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(a => a.Definition.Name)
                    .ToList());

        return new CampRoleProjection(specialRoleUserIds, memberRoleNames);
    }

    internal static CampInfo CreateCampInfo(
        Camp camp,
        bool includeEarlyEntryGrantCount = true,
        CampRoleProjection? roles = null)
    {
        return new CampInfo(
            camp.Id,
            camp.Slug,
            camp.ContactEmail,
            camp.ContactPhone,
            camp.IsSwissCamp,
            camp.TimesAtNowhere,
            camp.Seasons
                .Select(s => CreateCampSeasonInfo(s, camp.Slug, includeEarlyEntryGrantCount, roles))
                .ToList())
        {
            WebOrSocialUrl = camp.WebOrSocialUrl,
            Links = camp.Links,
            HideHistoricalNames = camp.HideHistoricalNames,
            HistoricalNames = camp.HistoricalNames
                .Select(name => name.Name)
                .ToList(),
            Images = camp.Images
                .Select(image => new CampImageSummary(image.Id, $"/{image.StoragePath}", image.SortOrder))
                .ToList()
        };
    }

    internal static CampSeasonInfo CreateCampSeasonInfo(
        CampSeason season,
        string campSlug,
        bool includeEarlyEntryGrantCount = false,
        CampRoleProjection? roles = null)
    {
        return new CampSeasonInfo(
            season.Id,
            season.CampId,
            campSlug,
            season.Year,
            season.NameLockDate,
            season.Name,
            season.BlurbShort,
            season.Languages,
            season.Vibes.ToList(),
            season.Status,
            season.AcceptingMembers,
            season.KidsWelcome,
            season.AdultPlayspace,
            season.MemberCount,
            season.SoundZone,
            season.SpaceRequirement,
            season.ElectricalGrid,
            season.EeSlotCount,
            includeEarlyEntryGrantCount
                ? season.Members.Count(m => m.Status == CampMemberStatus.Active && m.HasEarlyEntry)
                : null,
            includeEarlyEntryGrantCount
                ? season.Members.Count(m => m.Status == CampMemberStatus.Active)
                : null)
        {
            EventSettingsId = season.EventSettingsId,
            BlurbLong = season.BlurbLong,
            KidsVisiting = season.KidsVisiting,
            KidsAreaDescription = season.KidsAreaDescription,
            HasPerformanceSpace = season.HasPerformanceSpace,
            PerformanceTypes = season.PerformanceTypes,
            Members = season.Members
                .Where(m => m.Status != CampMemberStatus.Removed)
                .Select(m => new CampSeasonMemberInfo(
                    m.Id,
                    m.UserId,
                    m.Status,
                    m.RequestedAt,
                    m.ConfirmedAt,
                    m.HasEarlyEntry)
                {
                    Roles = roles?.MemberRoleNames.TryGetValue(m.Id, out var names) == true
                        ? names
                        : []
                })
                .ToList(),
            LeadUserIds = GetSpecialRoleUserIds(season.Id, CampSpecialRole.Lead, roles?.SpecialRoleUserIds),
            WorkshopLeadUserIds = GetSpecialRoleUserIds(season.Id, CampSpecialRole.Workshop, roles?.SpecialRoleUserIds)
        };
    }

    private static IReadOnlyList<Guid> GetSpecialRoleUserIds(
        Guid campSeasonId,
        CampSpecialRole role,
        IReadOnlyDictionary<(Guid CampSeasonId, CampSpecialRole Role), IReadOnlyList<Guid>>? specialRoleUserIds)
    {
        return specialRoleUserIds is not null
               && specialRoleUserIds.TryGetValue((campSeasonId, role), out var userIds)
            ? userIds
            : [];
    }

    internal static CampEditData CreateCampEditData(Camp camp, CampSeason season, Guid? eventSettingsId, LocalDate today)
    {
        return new CampEditData(
            camp.Id,
            camp.Slug,
            season.Id,
            season.Year,
            eventSettingsId,
            season.NameLockDate.HasValue && today >= season.NameLockDate.Value,
            season.Name,
            camp.ContactEmail,
            camp.ContactPhone,
            camp.Links is { Count: > 0 }
                ? camp.Links.Select(l => l.Url).ToList()
                : camp.WebOrSocialUrl is not null
                    ? [camp.WebOrSocialUrl]
                    : [],
            camp.IsSwissCamp,
            camp.HideHistoricalNames,
            camp.TimesAtNowhere,
            season.BlurbLong,
            season.BlurbShort,
            season.Languages,
            season.AcceptingMembers,
            season.KidsWelcome,
            season.KidsVisiting,
            season.KidsAreaDescription,
            season.HasPerformanceSpace,
            season.PerformanceTypes,
            season.Vibes.ToList(),
            season.AdultPlayspace,
            season.MemberCount,
            season.SpaceRequirement,
            season.SoundZone,
            season.ElectricalGrid,
            camp.Images
                .Select(i => new CampImageSummary(i.Id, $"/{i.StoragePath}", i.SortOrder))
                .ToList(),
            camp.HistoricalNames
                .Select(h => new CampHistoricalNameSummary(h.Id, h.Name, h.Year, h.Source.ToString()))
                .ToList());
    }

    public async Task<CampInfo?> GetCampBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        var camp = await repo.GetBySlugAsync(slug, cancellationToken);
        // GetBySlugAsync does not load Seasons.Members, so EE/member counts are
        // unknown here — emit null rather than a misleading 0.
        if (camp is null) return null;
        var roles = await GetRoleProjectionForYearsAsync(
            camp.Seasons.Select(season => season.Year).Distinct().ToList(),
            cancellationToken);
        return CreateCampInfo(camp, includeEarlyEntryGrantCount: false, roles);
    }

    public async Task<CampInfo?> GetCampByIdAsync(Guid campId, CancellationToken cancellationToken = default)
    {
        // GetByIdAsync loads Seasons.Members, so the EE grant count projects correctly here
        // (unlike the by-slug read above).
        var camp = await repo.GetByIdAsync(campId, cancellationToken);
        if (camp is null) return null;
        var roles = await GetRoleProjectionForYearsAsync(
            camp.Seasons.Select(season => season.Year).Distinct().ToList(),
            cancellationToken);
        return CreateCampInfo(camp, roles: roles);
    }

    public async Task<CampEditData?> GetCampEditDataAsync(
        Guid campId,
        Guid? eventSettingsId = null,
        CancellationToken cancellationToken = default)
    {
        var camp = await repo.GetByIdAsync(campId, cancellationToken);
        if (camp is null)
        {
            return null;
        }

        var targetEventId = eventSettingsId
            ?? (await settingsService.GetActiveEventSettingsAsync(cancellationToken))?.Id;
        var season = camp.Seasons
            .FirstOrDefault(s => targetEventId is not null && s.EventSettingsId == targetEventId)
            ?? camp.Seasons
                .OrderByDescending(s => s.Year)
                .ThenByDescending(s => s.CreatedAt)
                .FirstOrDefault();

        if (season is null)
        {
            return null;
        }

        return CreateCampEditData(
            camp,
            season,
            season.EventSettingsId,
            clock.GetCurrentInstant().InUtc().Date);
    }

    public async Task<IReadOnlyList<CampInfo>> GetCampsForYearAsync(
        int year, CancellationToken cancellationToken = default)
    {
        var camps = await repo.GetCampsWithLeadsForYearAsync(
            year, statusFilter: null, cancellationToken);
        var roles = await GetRoleProjectionForYearsAsync([year], cancellationToken);
        return camps.Select(c => CreateCampInfo(c, roles: roles)).ToList();
    }

    public async Task<CampSeasonInfo?> GetCampSeasonByIdAsync(
        Guid campSeasonId, CancellationToken cancellationToken = default)
    {
        var season = await repo.GetSeasonByIdAsync(campSeasonId, cancellationToken);
        if (season is null) return null;
        var roles = await GetRoleProjectionForYearsAsync([season.Year], cancellationToken);
        return CreateCampSeasonInfo(
            season,
            season.Camp?.Slug ?? string.Empty,
            roles: roles);
    }

    public async Task<CampMemberLookup?> GetCampMemberStatusAsync(Guid campMemberId, CancellationToken cancellationToken = default)
    {
        var row = await repo.GetMemberLookupAsync(campMemberId, cancellationToken);
        return row is null ? null : new CampMemberLookup(row.Value.CampSeasonId, row.Value.UserId, row.Value.Status);
    }
}
