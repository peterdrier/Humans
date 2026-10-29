namespace Humans.Camps.Models;

internal sealed class CampAdminPageBuilder(
    ICampService campService,
    ICampRoleService campRoleService)
{
    public async Task<CampAdminViewModel> BuildAsync(CancellationToken ct = default)
    {
        var settings = await campService.GetSettingsAsync(ct);
        // One season per camp: the active event's.
        var allCamps = settings.ActiveEventSettingsId is { } activeEventId
            ? await campService.GetCampsForEventAsync(activeEventId, ct)
            : [];

        var withdrawnSeasons = BuildCampCards(allCamps, CampSeasonStatus.Withdrawn);

        var activeStatuses = new HashSet<CampSeasonStatus> { CampSeasonStatus.Active, CampSeasonStatus.Full };
        var campsWithLeads = allCamps
            .Where(c => c.Seasons.Any(s => activeStatuses.Contains(s.Status)))
            .ToList();
        var summaries = BuildSummaries(campsWithLeads);

        var missingSpecialRoles = await campRoleService.GetMissingSpecialRolesAsync(ct);

        return new CampAdminViewModel
        {
            ActiveEventName = settings.ActiveEvent?.EventName ?? "No active event",
            HasMissingSpecialRoles = missingSpecialRoles.Count > 0,
            OpenEventNames = [.. settings.OpenEvents.Select(e => e.EventName)],
            TotalCamps = allCamps.Count,
            ActiveCamps = campsWithLeads.Count,
            WithdrawnCamps = withdrawnSeasons,
            Events = settings.Events,
            NameLockDates = settings.NameLockDates,
            AllCampSummaries = summaries,
            PendingCamps = BuildCampCards(allCamps, CampSeasonStatus.Pending)
        };
    }

    private static List<CampCardViewModel> BuildCampCards(
        IReadOnlyList<CampInfo> camps,
        CampSeasonStatus status) =>
        camps
            .SelectMany(camp => camp.Seasons
                .Where(season => season.Status == status)
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
                AcceptingMembers = season?.AcceptingMembers.ToString() ?? "—",
                MemberCount = season?.MemberCount ?? 0,
                Zone = season?.SoundZone?.ToString() ?? "—",
                SpaceRequirement = season?.SpaceRequirement?.ToString() ?? "—",
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
        return rows
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
