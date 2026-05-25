using Humans.Application.DTOs.VolunteerTrackingExport;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using NodaTime;

namespace Humans.Application.Services.Shifts;

/// <summary>
/// Builds the <see cref="VolunteerExportModel"/> consumed by the XLSX builder.
/// Composes the repo read (confirmed shifts in range), the event time-zone lookup
/// (via <see cref="IShiftManagementService.GetByIdAsync"/>), and per-user
/// <see cref="UserInfo"/> resolution. Trusts the repo's status filter — no
/// re-check on <see cref="Humans.Domain.Enums.SignupStatus"/>.
/// </summary>
public sealed class VolunteerTrackingExportService(
    IVolunteerTrackingRepository repository,
    IShiftManagementService shiftManagementService,
    IUserServiceRead userService)
    : IVolunteerTrackingExportService, IEarlyEntryProvider
{
    private readonly IVolunteerTrackingRepository _repository = repository;
    private readonly IShiftManagementService _shiftManagementService = shiftManagementService;
    private readonly IUserServiceRead _userService = userService;

    public async Task<VolunteerExportModel> BuildAsync(VolunteerExportRequest request, CancellationToken ct)
    {
        var days = EnumerateDays(request.StartDate, request.EndDate);
        var shifts = await _repository.GetConfirmedShiftsInRangeAsync(
            request.EventSettingsId, request.StartDate, request.EndDate, request.DepartmentId, ct);

        // Resolve team names via the Teams-aware service surface (the Shifts repo
        // deliberately returns TeamId only — no cross-section db.Teams query).
        var depts = await _shiftManagementService.GetDepartmentsWithRotasAsync(request.EventSettingsId);
        var teamNames = depts
            .GroupBy(d => d.TeamId)
            .ToDictionary(g => g.Key, g => g.First().TeamName);

        // Filtered team name (if filtered) for the filename + summary.
        string? filteredTeamName = request.DepartmentId is Guid deptId
            ? teamNames.GetValueOrDefault(deptId)
            : null;

        if (shifts.Count == 0)
            return BuildEmptyModel(request, days, filteredTeamName);

        // Look up the event time zone via the existing IShiftManagementService surface
        // (EventSettings.TimeZoneId is the IANA id). Avoids a new interface method.
        var eventSettings = await _shiftManagementService.GetByIdAsync(request.EventSettingsId)
            ?? throw new InvalidOperationException($"EventSettings {request.EventSettingsId} not found.");
        var zone = DateTimeZoneProviders.Tzdb[eventSettings.TimeZoneId];

        // (1) Build (userId, day) → list of (teamId, teamName, hours) for the range.
        var perUserPerDay = BucketByUserDayTeam(shifts, days, zone, teamNames);

        // (2) Per user: primary team = team with most total hours; first-shift date.
        var userIds = perUserPerDay.Keys.Select(k => k.userId).Distinct().ToList();
        var playaNames = await LoadPlayaNamesAsync(userIds, ct);
        var firstShiftDay = ComputeFirstShiftDay(shifts, zone);
        var primaryTeam = ComputePrimaryTeam(perUserPerDay);

        // (3) Build cells per user.
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

        // (4) Group by primary team. Order by total team hours desc, tie-break alphabetical.
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

        return BuildModel(request, days, groups, totals, filteredTeamName);
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
            var localStart = s.StartsAtUtc.InZone(zone).LocalDateTime;
            var localEnd = s.EndsAtUtc.InZone(zone).LocalDateTime;
            var firstDay = LocalDate.Max(localStart.Date, rangeStart);
            var lastDay = LocalDate.Min(localEnd.Date, rangeEnd);
            for (var d = firstDay; d <= lastDay; d = d.PlusDays(1))
            {
                var dayStart = d.AtStartOfDayInZone(zone).LocalDateTime;
                var dayEnd = d.PlusDays(1).AtStartOfDayInZone(zone).LocalDateTime;
                var overlapStart = LocalDateTime.Max(dayStart, localStart);
                var overlapEnd = LocalDateTime.Min(dayEnd, localEnd);
                var hours = (overlapEnd - overlapStart).ToDuration().TotalHours;
                if (hours <= 0) continue;
                var key = (s.UserId, d);
                if (!result.TryGetValue(key, out var list))
                    result[key] = list = [];
                list.Add((s.TeamId, teamNames.GetValueOrDefault(s.TeamId, string.Empty), hours));
            }
        }
        return result;
    }

    private static Dictionary<Guid, LocalDate> ComputeFirstShiftDay(IReadOnlyList<ConfirmedShiftRow> shifts, DateTimeZone zone) =>
        ShiftEarlyEntryProjection.FirstShiftDayByUser(shifts, zone);

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

    // Intentionally sequential — at ~500 humans (CLAUDE.md scale guidance), the round-trip
    // cost is negligible and sequential code is easier to debug than parallel awaits.
    private async Task<Dictionary<Guid, string>> LoadPlayaNamesAsync(IReadOnlyList<Guid> userIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, string>();
        foreach (var id in userIds)
        {
            var info = await _userService.GetUserInfoAsync(id, ct);
            result[id] = info?.BurnerName ?? "(unknown)";
        }
        return result;
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
                if (firstShiftDay[userId] > d) continue;        // hasn't arrived yet
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

    private static string BuildMethodologyBlurb() =>
        "Rows = humans with >=1 confirmed shift in range. Cell color = the team they worked most " +
        "hours that day. White cell = day before their first confirmed shift (arrival day). " +
        "Totals row = humans on-site that day (used for meal counts). Names shown are playa names.";

    private static string BuildFileName(VolunteerExportRequest req, string? departmentSlug)
    {
        var prefix = departmentSlug is { Length: > 0 } slug
            ? $"volunteer-tracking-{slug}-"
            : "volunteer-tracking-";
        return $"{prefix}{req.StartDate:yyyy-MM-dd}-to-{req.EndDate:yyyy-MM-dd}.xlsx";
    }

    private static string SlugifyTeamName(string teamName)
    {
        // Spec §File Output slugification rule:
        //   1) lowercase, 2) strip diacritics (NFD + drop combining marks),
        //   3) non-[a-z0-9] -> '-', 4) collapse repeats, 5) trim '-', 6) fall back to "team".
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
        var es = await _shiftManagementService.GetActiveAsync();
        if (es is null) return [];

        var start = es.GateOpeningDate.PlusDays(es.BuildStartOffset);
        var end = es.GateOpeningDate.PlusDays(-1);
        var rows = await _repository.GetConfirmedShiftsInRangeAsync(es.Id, start, end, departmentId: null, ct);
        if (rows.Count == 0) return [];

        var depts = await _shiftManagementService.GetDepartmentsWithRotasAsync(es.Id);
        var teamNames = depts
            .GroupBy(d => d.TeamId)
            .ToDictionary(g => g.Key, g => g.First().TeamName);

        var zone = DateTimeZoneProviders.Tzdb[es.TimeZoneId];
        return ShiftEarlyEntryProjection.Project(rows, zone, teamNames);
    }

    private static VolunteerExportModel BuildEmptyModel(VolunteerExportRequest request, IReadOnlyList<LocalDate> days, string? filteredTeamName)
    {
        return BuildModel(request, days, Array.Empty<DepartmentGroup>(), new int[days.Count], filteredTeamName);
    }

    private static VolunteerExportModel BuildModel(
        VolunteerExportRequest request,
        IReadOnlyList<LocalDate> days,
        IReadOnlyList<DepartmentGroup> groups,
        IReadOnlyList<int> totals,
        string? filteredTeamName)
    {
        var deptName = filteredTeamName ?? "All";
        var periodLabel = request.Period?.ToString() ?? "custom";
        var slug = filteredTeamName is null ? null : SlugifyTeamName(filteredTeamName);
        return new VolunteerExportModel(
            MethodologyBlurb: BuildMethodologyBlurb(),
            FilterSummary: $"Department: {deptName} - Range: {request.StartDate} -> {request.EndDate} ({periodLabel})",
            GeneratedAtUtc: request.GeneratedAtUtc,
            GeneratedByName: request.ActorPlayaName,
            Days: days,
            Groups: groups,
            TotalsPerDay: totals,
            SuggestedFileName: BuildFileName(request, slug));
    }
}
