using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.CityPlanning;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;

namespace Humans.Web.Models.CampAdmin;

public sealed class CampAdminPageBuilder
{
    private readonly ICampService _campService;
    private readonly ICityPlanningService _cityPlanningService;
    private readonly IUserService _userService;

    public CampAdminPageBuilder(
        ICampService campService,
        ICityPlanningService cityPlanningService,
        IUserService userService)
    {
        _campService = campService;
        _cityPlanningService = cityPlanningService;
        _userService = userService;
    }

    public async Task<CampAdminViewModel> BuildAsync()
    {
        var settings = await _campService.GetSettingsAsync();
        var registrationInfo = await _cityPlanningService.GetRegistrationInfoAsync();
        var allCamps = await _campService.GetCampsForYearAsync(settings.PublicYear);
        var pendingSeasons = await _campService.GetPendingSeasonsAsync();
        var openSeasons = settings.OpenSeasons.ToList();
        var nameLockDates = openSeasons.Count > 0
            ? await _campService.GetNameLockDatesAsync(openSeasons)
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

        var activeStatuses = new[] { CampSeasonStatus.Active, CampSeasonStatus.Full };
        var campsWithLeads = await _campService.GetCampsWithLeadsForYearAsync(settings.PublicYear, activeStatuses);
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

    private async Task<List<CampSummaryRowViewModel>> BuildSummariesAsync(IReadOnlyList<CampInfo> campsWithLeads)
    {
        var leadUserIds = campsWithLeads
            .SelectMany(c => (c.Leads ?? []).Select(l => l.UserId))
            .Distinct()
            .ToList();
        var leadUsers = await _userService.GetByIdsAsync(leadUserIds);

        return campsWithLeads.Select(c =>
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
                Leads = (c.Leads ?? [])
                    .Select(l => new CampLeadViewModel
                    {
                        LeadId = l.Id,
                        UserId = l.UserId,
                        DisplayName = leadUsers.TryGetValue(l.UserId, out var u) ? u.DisplayName : string.Empty
                    }).ToList()
            };
        }).ToList();
    }
}
