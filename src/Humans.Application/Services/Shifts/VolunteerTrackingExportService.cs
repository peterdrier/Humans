using Humans.Application.DTOs.VolunteerTrackingExport;
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
    IUserService userService)
    : IVolunteerTrackingExportService
{
    private readonly IVolunteerTrackingRepository _repository = repository;
    private readonly IShiftManagementService _shiftManagementService = shiftManagementService;
    private readonly IUserService _userService = userService;

    public async Task<VolunteerExportModel> BuildAsync(VolunteerExportRequest request, CancellationToken ct)
    {
        var days = EnumerateDays(request.StartDate, request.EndDate);
        var shifts = await _repository.GetConfirmedShiftsInRangeAsync(
            request.EventSettingsId, request.StartDate, request.EndDate, request.DepartmentId, ct);

        // Resolve the filtered team name (if filtered) for the filename + summary.
        string? filteredTeamName = null;
        if (request.DepartmentId is Guid deptId)
        {
            var depts = await _shiftManagementService.GetDepartmentsWithRotasAsync(request.EventSettingsId);
            filteredTeamName = depts.FirstOrDefault(d => d.TeamId == deptId).TeamName;
        }

        // Group construction lands in Task 3.4. For now satisfy the empty-shifts contract.
        _ = shifts;
        return BuildEmptyModel(request, days, filteredTeamName);
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
