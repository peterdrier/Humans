using Humans.Shifts.Services.Dtos;
using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.EarlyEntry.Contracts;
using Humans.Shifts.Contracts;
using NodaTime;
using Humans.Shifts.Data;
using Humans.Users.Contracts;

namespace Humans.Shifts.Services;

/// <summary>Trusts the repository's status filter; no re-check on <see cref="SignupStatus"/>.</summary>
internal sealed class VolunteerTrackingExportService(
    IShiftManagementRepository shiftManagementRepository,
    IShiftManagement shiftManagementService,
    IBurnSettingsService burnSettingsService,
    IUserService userService,
    IAuditLogService audit)
    : IVolunteerTrackingExportService, IEarlyEntryProvider
{
    private readonly IShiftManagementRepository _shiftManagementRepository = shiftManagementRepository;
    private readonly IShiftManagement _shiftManagementService = shiftManagementService;
    private readonly IBurnSettingsService _burnSettingsService = burnSettingsService;
    private readonly IUserService _userService = userService;
    private readonly IAuditLogService _audit = audit;

    public async Task<VolunteerExportModel> BuildAsync(VolunteerExportRequest request, CancellationToken ct)
    {
        var eventSettings = await _burnSettingsService.GetByIdAsync(request.EventSettingsId, ct)
            ?? throw new InvalidOperationException($"EventSettings {request.EventSettingsId} not found.");
        var zone = DateTimeZoneProviders.Tzdb[eventSettings.TimeZoneId];

        var days = EnumerateDays(request.StartDate, request.EndDate);
        var shifts = await _shiftManagementRepository.GetConfirmedShiftsInRangeAsync(
            request.EventSettingsId, eventSettings, request.StartDate, request.EndDate, request.DepartmentId, ct);

        // The repository returns TeamId only; names come through the Teams-aware service.
        var depts = await _shiftManagementService.GetDepartmentsWithRotasAsync(request.EventSettingsId);
        var teamNames = depts
            .GroupBy(d => d.TeamId)
            .ToDictionary(g => g.Key, g => g.First().TeamName);

        string? filteredTeamName = request.DepartmentId is Guid deptId
            ? teamNames.GetValueOrDefault(deptId)
            : null;

        if (shifts.Count == 0)
            return await BuildModelAsync(request, days, Array.Empty<DepartmentGroup>(), new int[days.Count], filteredTeamName);

        var perUserPerDay = BucketByUserDayTeam(shifts, days, zone, teamNames);

        // Primary team: the one with most hours.
        var userIds = perUserPerDay.Keys.Select(k => k.userId).Distinct().ToList();
        var playaNames = await LoadPlayaNamesAsync(userIds, ct);
        var firstShiftDay = ShiftEarlyEntryProjection.FirstShiftDayByUser(shifts, zone);
        var primaryTeam = ComputePrimaryTeam(perUserPerDay);

        var rows = new Dictionary<Guid, HumanRow>();
        foreach (var userId in userIds)
        {
            var cells = new CellState[days.Count];
            var arrivalDay = firstShiftDay[userId].PlusDays(-1);
            for (var i = 0; i < days.Count; i++)
            {
                var d = days[i];
                if (perUserPerDay.TryGetValue((userId, d), out var teamsThatDay))
                {
                    var winner = teamsThatDay
                        .GroupBy(t => (t.teamId, t.teamName))
                        .Select(g => (g.Key.teamId, g.Key.teamName, hours: g.Sum(x => x.hours)))
                        .OrderByDescending(t => t.hours)
                        .ThenBy(t => t.teamName, StringComparer.OrdinalIgnoreCase)
                        .First();
                    cells[i] = CellState.Worked(winner.teamId, TeamPalette.ColorFor(winner.teamId));
                }
                else if (d == arrivalDay)
                {
                    cells[i] = CellState.Arrival;
                }
                else
                {
                    cells[i] = CellState.Empty;
                }
            }
            rows[userId] = new HumanRow(userId, playaNames[userId], cells);
        }

        // By total team hours, ties alphabetical.
        var totalTeamHours = perUserPerDay
            .SelectMany(kvp => kvp.Value)
            .GroupBy(v => v.teamId)
            .ToDictionary(g => g.Key, g => g.Sum(v => v.hours));

        var groups = rows
            .GroupBy(r => primaryTeam[r.Key])
            .Select(g =>
            {
                var (teamId, teamName) = g.Key;
                var humans = g.Select(r => r.Value)
                    .OrderBy(h => firstShiftDay[h.UserId])
                    .ThenBy(h => h.PlayaName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new DepartmentGroup(teamId, teamName, TeamPalette.ColorFor(teamId), humans);
            })
            .OrderByDescending(dg => totalTeamHours[dg.TeamId])
            .ThenBy(dg => dg.TeamName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totals = ComputeTotals(days, rows, firstShiftDay);

        return await BuildModelAsync(request, days, groups, totals, filteredTeamName);
    }

    private static Dictionary<(Guid userId, LocalDate day), List<(Guid teamId, string teamName, double hours)>>
        BucketByUserDayTeam(
            IReadOnlyList<ConfirmedShiftRow> shifts,
            IReadOnlyList<LocalDate> days,
            DateTimeZone zone,
            IReadOnlyDictionary<Guid, string> teamNames)
    {
        var result = new Dictionary<(Guid, LocalDate), List<(Guid, string, double)>>();
        var rangeStart = days[0];
        var rangeEnd = days[^1];
        foreach (var s in shifts)
        {
            var firstDay = LocalDate.Max(s.StartsAtUtc.InZone(zone).Date, rangeStart);
            var lastDay = LocalDate.Min(s.EndsAtUtc.InZone(zone).Date, rangeEnd);
            for (var d = firstDay; d <= lastDay; d = d.PlusDays(1))
            {
                var dayStart = d.AtStartOfDayInZone(zone).ToInstant();
                var dayEnd = d.PlusDays(1).AtStartOfDayInZone(zone).ToInstant();
                var overlapStart = Instant.Max(dayStart, s.StartsAtUtc);
                var overlapEnd = Instant.Min(dayEnd, s.EndsAtUtc);
                var hours = (overlapEnd - overlapStart).TotalHours;
                if (hours <= 0) continue;
                var key = (s.UserId, d);
                if (!result.TryGetValue(key, out var list))
                    result[key] = list = [];
                list.Add((s.TeamId, teamNames.GetValueOrDefault(s.TeamId, string.Empty), hours));
            }
        }
        return result;
    }

    private static Dictionary<Guid, (Guid teamId, string teamName)> ComputePrimaryTeam(
        Dictionary<(Guid userId, LocalDate day), List<(Guid teamId, string teamName, double hours)>> bucket)
    {
        return bucket
            .SelectMany(kvp => kvp.Value.Select(v => (kvp.Key.userId, v.teamId, v.teamName, v.hours)))
            .GroupBy(t => (t.userId, t.teamId, t.teamName))
            .Select(g => (g.Key.userId, g.Key.teamId, g.Key.teamName, hours: g.Sum(x => x.hours)))
            .GroupBy(t => t.userId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(t => t.hours)
                      .ThenBy(t => t.teamName, StringComparer.OrdinalIgnoreCase)
                      .ThenBy(t => t.teamId)
                      .Select(t => (t.teamId, t.teamName))
                      .First());
    }

    private async Task<Dictionary<Guid, string>> LoadPlayaNamesAsync(IReadOnlyList<Guid> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0) return [];
        var infos = await _userService.GetUserInfosAsync(userIds, ct);
        return userIds.ToDictionary(id => id, id => infos.GetValueOrDefault(id)?.BurnerName ?? "(unknown)");
    }

    private static int[] ComputeTotals(
        IReadOnlyList<LocalDate> days,
        Dictionary<Guid, HumanRow> rows,
        Dictionary<Guid, LocalDate> firstShiftDay)
    {
        var totals = new int[days.Count];
        for (var i = 0; i < days.Count; i++)
        {
            var d = days[i];
            var count = 0;
            foreach (var (userId, row) in rows)
            {
                if (firstShiftDay[userId] > d) continue;
                if (row.Cells[i].Kind == CellKind.Worked) count++;
            }
            totals[i] = count;
        }
        return totals;
    }

    private static IReadOnlyList<LocalDate> EnumerateDays(LocalDate start, LocalDate end)
    {
        var count = Period.DaysBetween(start, end) + 1;
        var days = new LocalDate[count];
        for (var i = 0; i < count; i++) days[i] = start.PlusDays(i);
        return days;
    }

    private static string SlugifyTeamName(string teamName)
    {
        // The spec's slug rule: lowercase, strip diacritics, non-[a-z0-9] to '-', collapse, trim, else "team".
        var lower = teamName.ToLowerInvariant();
        var nfd = lower.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(nfd.Length);
        foreach (var ch in nfd)
        {
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '-');
        }
        var collapsed = CollapseHyphens(sb.ToString()).Trim('-');
        return collapsed.Length > 0 ? collapsed : "team";
    }

    private static string CollapseHyphens(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        var lastWasHyphen = false;
        foreach (var c in s)
        {
            if (c == '-')
            {
                if (!lastWasHyphen) sb.Append('-');
                lastWasHyphen = true;
            }
            else
            {
                sb.Append(c);
                lastWasHyphen = false;
            }
        }
        return sb.ToString();
    }

    public async Task<IReadOnlyList<EarlyEntryGrant>> GetEarlyEntriesAsync(CancellationToken ct)
    {
        var es = await _burnSettingsService.GetActiveAsync(ct);
        if (es is null) return [];

        var start = es.GateOpeningDate.PlusDays(es.BuildStartOffset);
        var end = es.GateOpeningDate.PlusDays(-1);
        var rows = await _shiftManagementRepository.GetConfirmedShiftsInRangeAsync(es.Id, es, start, end, departmentId: null, ct);
        if (rows.Count == 0) return [];

        var depts = await _shiftManagementService.GetDepartmentsWithRotasAsync(es.Id);
        var teamNames = depts
            .GroupBy(d => d.TeamId)
            .ToDictionary(g => g.Key, g => g.First().TeamName);

        var zone = DateTimeZoneProviders.Tzdb[es.TimeZoneId];
        return ShiftEarlyEntryProjection.Project(rows, zone, teamNames);
    }

    private async Task<VolunteerExportModel> BuildModelAsync(
        VolunteerExportRequest request,
        IReadOnlyList<LocalDate> days,
        IReadOnlyList<DepartmentGroup> groups,
        IReadOnlyList<int> totals,
        string? filteredTeamName)
    {
        var deptName = filteredTeamName ?? "All";
        var periodLabel = request.Period?.ToString() ?? "custom";
        var slug = filteredTeamName is null ? null : SlugifyTeamName(filteredTeamName);
        var filePrefix = slug is { Length: > 0 }
            ? $"volunteer-tracking-{slug}-"
            : "volunteer-tracking-";
        var model = new VolunteerExportModel(
            MethodologyBlurb: "Rows = humans with >=1 confirmed shift in range. Cell color = the team they worked most " +
                "hours that day. White cell = day before their first confirmed shift (arrival day). " +
                "Totals row = humans on-site that day (used for meal counts). Names shown are playa names.",
            FilterSummary: $"Department: {deptName} - Range: {request.StartDate} -> {request.EndDate} ({periodLabel})",
            GeneratedAtUtc: request.GeneratedAtUtc,
            GeneratedByName: request.ActorPlayaName,
            Days: days,
            Groups: groups,
            TotalsPerDay: totals,
            SuggestedFileName: $"{filePrefix}{request.StartDate.ToInvariantDate()}-to-{request.EndDate.ToInvariantDate()}.xlsx");
        await _audit.LogAsync(AuditAction.VolunteerTrackingExported, "EventSettings", request.EventSettingsId,
            $"Volunteer tracking XLSX exported: {groups.Sum(group => group.Humans.Count)} people rows, " +
            $"range {request.StartDate.ToInvariantDate()} to {request.EndDate.ToInvariantDate()}, " +
            $"department {request.DepartmentId?.ToString() ?? "all"}", request.ActorUserId);
        return model;
    }
}
