using Humans.Base.Enums;
using Humans.Users.Contracts;

namespace Humans.Camps.Models;

internal sealed class CampDirectoryPageBuilder(
    ICampManagement campService,
    ICampRoleService campRoleService)
{
    public async Task<(CampIndexViewModel Model, int PendingCount)> BuildAsync(
        UserInfo? user, CampFilterViewModel? filters, CancellationToken ct = default)
    {
        var settings = await campService.GetSettingsAsync(ct);
        var activeEventId = settings.ActiveEventSettingsId;
        var camps = activeEventId is { } eventId
            ? await campService.GetCampsForEventAsync(eventId, ct)
            : [];

        var roleSummaries = filters?.ShowLeadPositions == true && activeEventId is not null
            ? await campRoleService.GetDirectoryRoleSummariesAsync(activeEventId.Value, ct)
            : null;

        var leadCampIds = user is null
            ? new HashSet<Guid>()
            : camps.Where(camp => camp.IsLead(user.Id)).Select(camp => camp.Id).ToHashSet();

        var directoryCards = ApplyDirectoryFilters(
            camps
                .Select(camp => new { Camp = camp, Season = camp.Seasons.FirstOrDefault() })
                .Where(row => row.Season is { IsPublic: true })
                .Select(row => MapCampCard(row.Camp, row.Season!, roleSummaries)),
            filters);

        var cards = directoryCards
            .OrderBy(card => leadCampIds.Contains(card.Id) ? 0 : 1)
            .ThenBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var myCamps = new List<CampCardViewModel>();
        if (user is not null)
        {
            myCamps = camps
                .Where(camp => leadCampIds.Contains(camp.Id))
                .Select(camp => new { Camp = camp, Season = camp.Seasons.FirstOrDefault() })
                .Where(row => row.Season is { IsPublic: false })
                .Select(row => MapCampCard(row.Camp, row.Season!, roleSummaries))
                .Where(card => cards.All(publicCard => publicCard.Id != card.Id))
                .ToList();
        }

        var pendingCount = camps
            .SelectMany(camp => camp.Seasons)
            .Count(season => season.Status == CampSeasonStatus.Pending);

        var viewModel = new CampIndexViewModel
        {
            Year = settings.ActiveEvent?.Year ?? settings.PublicYear,
            Camps = cards,
            MyCamps = myCamps,
            Filters = filters ?? new CampFilterViewModel()
        };

        return (viewModel, pendingCount);
    }

    private static IEnumerable<CampCardViewModel> ApplyDirectoryFilters(
        IEnumerable<CampCardViewModel> cards,
        CampFilterViewModel? filters)
    {
        if (filters?.Vibe.HasValue == true)
        {
            cards = cards.Where(card => card.Vibes.Contains(filters.Vibe.Value));
        }

        if (filters?.SoundZone.HasValue == true)
        {
            cards = cards.Where(card => card.SoundZone == filters.SoundZone.Value);
        }

        if (filters?.KidsFriendly == true)
        {
            cards = cards.Where(card => card.KidsWelcome == YesNoMaybe.Yes);
        }

        if (filters?.AcceptingMembers == true)
        {
            cards = cards.Where(card => card.AcceptingMembers == YesNoMaybe.Yes);
        }

        if (!string.IsNullOrWhiteSpace(filters?.Search))
        {
            var q = filters.Search.Trim();
            cards = cards.Where(card =>
                card.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        return cards;
    }

    private static CampCardViewModel MapCampCard(
        CampInfo camp,
        CampSeasonInfo season,
        IReadOnlyDictionary<Guid, IReadOnlyList<CampDirectoryRoleSummary>>? roleSummaries = null) => new()
        {
            Id = camp.Id,
            SeasonId = season.Id,
            Slug = camp.Slug,
            Name = season.Name,
            BlurbShort = season.BlurbShort,
            ImageUrl = camp.Images.MinBy(image => image.SortOrder)?.Url,
            Vibes = [.. season.Vibes],
            AcceptingMembers = season.AcceptingMembers,
            KidsWelcome = season.KidsWelcome,
            SoundZone = season.SoundZone,
            Status = season.Status,
            TimesAtNowhere = camp.TimesAtNowhere,
            LeadPositionPills = roleSummaries is not null && roleSummaries.TryGetValue(season.Id, out var summaries)
            ? summaries.OrderBy(s => s.SortOrder).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(s => new CampLeadPositionPillViewModel
                {
                    Name = s.Name,
                    FilledCount = s.Filled,
                    SlotCount = s.SlotCount
                }).ToList()
            : []
        };

}
