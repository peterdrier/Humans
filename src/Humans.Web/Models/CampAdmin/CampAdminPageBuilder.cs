using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.CityPlanning;
using Humans.Domain.Enums;

namespace Humans.Web.Models.CampAdmin;

public sealed class CampAdminPageBuilder(
    ICampServiceRead campService,
    ICampRoleService campRoleService,
    ICityPlanningService cityPlanningService)
{
    public async Task<CampAdminViewModel> BuildAsync()
    {
        var settings = await campService.GetSettingsAsync();
        var registrationInfo = await cityPlanningService.GetRegistrationInfoAsync();
        var allCamps = await campService.GetCampsForYearAsync(settings.PublicYear);
        var openSeasons = settings.OpenSeasons.ToList();

        var withdrawnSeasons = BuildCampCards(allCamps, settings.PublicYear, CampSeasonStatus.Withdrawn);

        var activeStatuses = new HashSet<CampSeasonStatus> { CampSeasonStatus.Active, CampSeasonStatus.Full };
        var campsWithLeads = allCamps
            .Where(c => c.Seasons.Any(s => s.Year == settings.PublicYear && activeStatuses.Contains(s.Status)))
            .ToList();
        var summaries = BuildSummaries(campsWithLeads);

        var missingSpecialRoles = await campRoleService.GetMissingSpecialRolesAsync();

        return new CampAdminViewModel
        {
            PublicYear = settings.PublicYear,
            HasMissingSpecialRoles = missingSpecialRoles.Count > 0,
            OpenSeasons = openSeasons,
            TotalCamps = allCamps.Count,
            ActiveCamps = allCamps.Count(b => b.Seasons.Any(s =>
                s.Year == settings.PublicYear && (s.Status == CampSeasonStatus.Active || s.Status == CampSeasonStatus.Full))),
            WithdrawnCamps = withdrawnSeasons,
            NameLockDates = settings.NameLockDates.ToDictionary(kv => kv.Key, kv => kv.Value),
            AllCampSummaries = summaries,
            RegistrationInfo = registrationInfo,
            EeStartDate = settings.EeStartDate,
            PendingCamps = BuildCampCards(allCamps, settings.PublicYear, CampSeasonStatus.Pending)
        };
    }

    private static List<CampCardViewModel> BuildCampCards(
        IReadOnlyList<CampInfo> camps,
        int year,
        CampSeasonStatus status) =>
        camps
            .SelectMany(camp => camp.Seasons
                .Where(season => season.Year == year && season.Status == status)
                .Select(season => new CampCardViewModel
                {
                    Id = camp.Id,
                    SeasonId = season.Id,
                    Slug = camp.Slug,
                    Name = season.Name,
                    BlurbShort = season.BlurbShort,
                    Status = season.Status
                }))
            .ToList();

    private static List<CampSummaryRowViewModel> BuildSummaries(IReadOnlyList<CampInfo> campsWithLeads)
    {
        var rows = new List<CampSummaryRowViewModel>(campsWithLeads.Count);
        foreach (var c in campsWithLeads)
        {
            var season = c.Seasons.FirstOrDefault();
            var leadUserIds = season?.LeadUserIds ?? [];
            rows.Add(new CampSummaryRowViewModel
            {
                Name = season?.Name ?? c.Slug,
                Slug = c.Slug,
                SeasonId = season?.Id,
                AcceptingMembers = season?.AcceptingMembers.ToString() ?? "â€”",
                MemberCount = season?.MemberCount ?? 0,
                Zone = season?.SoundZone?.ToString() ?? "â€”",
                SpaceRequirement = season?.SpaceRequirement?.ToString() ?? "â€”",
                YearsParticipating = c.TimesAtNowhere,
                EeSlotCount = season?.EeSlotCount ?? 0,
                EeGrantedCount = season?.EeGrantedCount ?? 0,
                JoinedMemberCount = season?.JoinedMemberCount ?? 0,
                Leads = leadUserIds
                    .Select(id => new CampLeadViewModel
                    {
                        LeadId = Guid.Empty,
                        UserId = id
                    }).ToList()
            });
        }
        return rows;
    }
}
