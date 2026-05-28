using Humans.Application.Interfaces.Camps;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using NodaTime;

namespace Humans.Application.Services.Camps;

internal static class CampReadModelProjection
{
    internal static IEnumerable<CampDirectoryCard> ApplyDirectoryFilter(
        IEnumerable<CampDirectoryCard> camps,
        CampDirectoryFilter? filter)
    {
        if (filter?.Vibe.HasValue == true)
        {
            camps = camps.Where(card => card.Vibes.Contains(filter.Vibe.Value));
        }

        if (filter?.SoundZone.HasValue == true)
        {
            camps = camps.Where(card => card.SoundZone == filter.SoundZone.Value);
        }

        if (filter?.KidsFriendly == true)
        {
            camps = camps.Where(card => card.KidsWelcome == YesNoMaybe.Yes);
        }

        if (filter?.AcceptingMembers == true)
        {
            camps = camps.Where(card => card.AcceptingMembers == YesNoMaybe.Yes);
        }

        if (!string.IsNullOrWhiteSpace(filter?.Search))
        {
            var q = filter.Search.Trim();
            camps = camps.Where(card =>
                card.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        return camps;
    }

    internal static CampDirectoryCard CreateCampDirectoryCard(Camp camp, int year)
    {
        var season = camp.Seasons.FirstOrDefault(s => s.Year == year);
        var firstImage = camp.Images.OrderBy(i => i.SortOrder).FirstOrDefault();

        return new CampDirectoryCard(
            camp.Id,
            camp.Slug,
            season?.Name ?? camp.Slug,
            season?.BlurbShort ?? string.Empty,
            firstImage is not null ? $"/{firstImage.StoragePath}" : null,
            season?.Vibes ?? [],
            season?.AcceptingMembers ?? YesNoMaybe.No,
            season?.KidsWelcome ?? YesNoMaybe.No,
            season?.SoundZone,
            season?.Status ?? CampSeasonStatus.Pending,
            camp.TimesAtNowhere);
    }

    internal static CampInfo CreateCampInfo(Camp camp, bool includeEarlyEntryGrantCount = true)
    {
        return new CampInfo(
            camp.Id,
            camp.Slug,
            camp.ContactEmail,
            camp.ContactPhone,
            camp.IsSwissCamp,
            camp.TimesAtNowhere,
            camp.Seasons.Select(s => CreateCampSeasonInfo(s, camp.Slug, includeEarlyEntryGrantCount)).ToList());
    }

    internal static CampSeasonInfo CreateCampSeasonInfo(
        CampSeason season,
        string campSlug,
        bool includeEarlyEntryGrantCount = false)
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
                : null);
    }

    internal static CampSettingsInfo CreateCampSettingsInfo(CampSettings settings) =>
        new(
            settings.PublicYear,
            settings.OpenSeasons.ToList(),
            settings.EeStartDate);

    internal static CampSeasonMemberInfo CreateCampSeasonMemberInfo(CampMember member) =>
        new(
            member.Id,
            member.UserId,
            member.Status,
            member.RequestedAt,
            member.ConfirmedAt,
            member.HasEarlyEntry);

    internal static CampDetailData CreateCampDetailData(Camp camp, CampSeason season, LocalDate today)
    {
        return new CampDetailData(
            camp.Id,
            camp.Slug,
            season.Name,
            CreateCampLinks(camp),
            camp.IsSwissCamp,
            camp.TimesAtNowhere,
            camp.HideHistoricalNames,
            camp.HistoricalNames.Select(h => h.Name).ToList(),
            camp.Images.OrderBy(i => i.SortOrder).Select(i => $"/{i.StoragePath}").ToList(),
            CreateCampSeasonDetailData(season, today));
    }

    internal static CampEditData CreateCampEditData(Camp camp, CampSeason season, LocalDate today)
    {
        return new CampEditData(
            camp.Id,
            camp.Slug,
            season.Id,
            season.Year,
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
                .OrderBy(i => i.SortOrder)
                .Select(i => new CampImageSummary(i.Id, $"/{i.StoragePath}", i.SortOrder))
                .ToList(),
            camp.HistoricalNames
                .Select(h => new CampHistoricalNameSummary(h.Id, h.Name, h.Year, h.Source.ToString()))
                .ToList());
    }

    internal static CampPublicSummary CreateCampPublicSummary(Camp camp, int year)
    {
        var season = camp.Seasons.FirstOrDefault(s => s.Year == year);
        var firstImage = camp.Images.OrderBy(i => i.SortOrder).FirstOrDefault();

        return new CampPublicSummary(
            camp.Id,
            camp.Slug,
            season?.Name ?? camp.Slug,
            season?.BlurbShort ?? string.Empty,
            season?.BlurbLong ?? string.Empty,
            firstImage is not null ? $"/{firstImage.StoragePath}" : null,
            (season?.Vibes ?? []).Select(vibe => vibe.ToString()).ToList(),
            (season?.AcceptingMembers ?? YesNoMaybe.No).ToString(),
            (season?.KidsWelcome ?? YesNoMaybe.No).ToString(),
            season?.SoundZone?.ToString(),
            (season?.Status ?? CampSeasonStatus.Pending).ToString(),
            camp.TimesAtNowhere,
            camp.IsSwissCamp,
            camp.Links,
            camp.WebOrSocialUrl);
    }

    internal static CampPlacementSummary? CreateCampPlacementSummary(Camp camp, int year)
    {
        var season = camp.Seasons.FirstOrDefault(s => s.Year == year);
        if (season is null)
        {
            return null;
        }

        return new CampPlacementSummary(
            camp.Id,
            camp.Slug,
            season.Name,
            season.MemberCount,
            season.SpaceRequirement?.ToString(),
            season.SoundZone?.ToString(),
            season.Status.ToString(),
            season.ElectricalGrid?.ToString());
    }

    private static IReadOnlyList<CampLink> CreateCampLinks(Camp camp)
    {
        if (camp.Links is { Count: > 0 })
        {
            return camp.Links;
        }

        return camp.WebOrSocialUrl is not null
            ? [new CampLink { Url = camp.WebOrSocialUrl }]
            : [];
    }

    private static CampSeasonDetailData CreateCampSeasonDetailData(CampSeason season, LocalDate today)
    {
        return new CampSeasonDetailData(
            season.Id,
            season.Year,
            season.Name,
            season.Status,
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
            season.NameLockDate.HasValue && today >= season.NameLockDate.Value);
    }
}
