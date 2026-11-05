using NodaTime;

namespace Humans.Camps.Services;

internal sealed class CampReadProjection(ICampRepository repo) : IApplicationService
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
                .OrderBy(m => m.RequestedAt)
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
}
