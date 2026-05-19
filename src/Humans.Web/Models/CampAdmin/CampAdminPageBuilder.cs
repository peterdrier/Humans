using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.CityPlanning;
using Humans.Domain.Enums;

namespace Humans.Web.Models.CampAdmin;

public sealed class CampAdminPageBuilder(ICampService campService, ICityPlanningService cityPlanningService)
{
    public async Task<CampAdminViewModel> BuildAsync()
    {
        var settings = await campService.GetSettingsAsync();
        var registrationInfo = await cityPlanningService.GetRegistrationInfoAsync();
        var allCamps = await campService.GetCampsForYearAsync(settings.PublicYear);
        var pendingSeasons = await campService.GetPendingSeasonsAsync();
        var openSeasons = settings.OpenSeasons.ToList();
        var nameLockDates = openSeasons.Count > 0
            ? await campService.GetNameLockDatesAsync(openSeasons)
            : new Dictionary<int, NodaTime.LocalDate?>();

        var withdrawnSeasons = allCamps
            .SelectMany(c => c.Seasons
                .Where(s => s.Year == settings.PublicYear && s.Status == CampSeasonStatus.Withdrawn)
                .Select(s => new CampCardViewModel
                {
                    Id = c.Id,
                    SeasonId = s.Id,
                    Slug = c.Slug,
                    Name = s.Name,
                    BlurbShort = s.BlurbShort,
                    Status = s.Status
                }))
            .ToList();

        // T-06: GetCampsForYearAsync always populates leads; filter by season
        // status in-memory.
        var activeStatuses = new HashSet<CampSeasonStatus> { CampSeasonStatus.Active, CampSeasonStatus.Full };
        var campsWithLeads = (await campService.GetCampsForYearAsync(settings.PublicYear))
            .Where(c => c.Seasons.Any(s => s.Year == settings.PublicYear && activeStatuses.Contains(s.Status)))
            .ToList();
        var summaries = await BuildSummariesAsync(campsWithLeads);

        return new CampAdminViewModel
        {
            PublicYear = settings.PublicYear,
            OpenSeasons = openSeasons,
            TotalCamps = allCamps.Count,
            ActiveCamps = allCamps.Count(b => b.Seasons.Any(s =>
                s.Year == settings.PublicYear && (s.Status == CampSeasonStatus.Active || s.Status == CampSeasonStatus.Full))),
            WithdrawnCamps = withdrawnSeasons,
            NameLockDates = nameLockDates,
            AllCampSummaries = summaries,
            RegistrationInfo = registrationInfo,
            EeStartDate = settings.EeStartDate,
            PendingCamps = pendingSeasons.Select(s => new CampCardViewModel
            {
                Id = s.CampId,
                SeasonId = s.Id,
                Slug = s.CampSlug,
                Name = s.Name,
                BlurbShort = s.BlurbShort,
                Status = s.Status
            }).ToList()
        };
    }

    private Task<List<CampSummaryRowViewModel>> BuildSummariesAsync(IReadOnlyList<CampInfo> campsWithLeads)
    {
        return Task.FromResult(campsWithLeads.Select(c =>
        {
            var season = c.Seasons.FirstOrDefault();
            return new CampSummaryRowViewModel
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
                Leads = c.Leads
                    .Select(l => new CampLeadViewModel
                    {
                        LeadId = l.Id,
                        UserId = l.UserId
                    }).ToList()
            };
        }).ToList());
    }
}
