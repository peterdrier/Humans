using Humans.Application.DTOs;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Services.Shifts;

public sealed class VolunteerTrackingService : IVolunteerTrackingService
{
    private readonly IVolunteerTrackingRepository _trackingRepo;
    private readonly IShiftManagementRepository _shiftManagement;
    private readonly IGeneralAvailabilityRepository _availability;
    private readonly IUserService _userService;
    private readonly IClock _clock;

    public VolunteerTrackingService(
        IVolunteerTrackingRepository trackingRepo,
        IShiftManagementRepository shiftManagement,
        IGeneralAvailabilityRepository availability,
        IUserService userService,
        IClock clock)
    {
        _trackingRepo = trackingRepo;
        _shiftManagement = shiftManagement;
        _availability = availability;
        _userService = userService;
        _clock = clock;
    }

    public async Task<VolunteerTrackingViewModel> GetTrackingDataAsync(CancellationToken ct = default)
    {
        var es = await _shiftManagement.GetActiveEventSettingsAsync(ct).ConfigureAwait(false);
        if (es is null)
        {
            return new VolunteerTrackingViewModel(
                false,
                0,
                default,
                default,
                Array.Empty<VolunteerHeatmapRow>(),
                Array.Empty<VolunteerCohortRow>());
        }

        var zone = DateTimeZoneProviders.Tzdb[es.TimeZoneId];
        var today = _clock.GetCurrentInstant().InZone(zone).Date;
        var todayOffset = Period.Between(es.GateOpeningDate, today, PeriodUnits.Days).Days;

        var signups = await _trackingRepo.GetEligibleBuildSignupsAsync(es.Id, ct).ConfigureAwait(false);
        var participations = await _userService.GetAllParticipationsForYearAsync(es.Year, ct).ConfigureAwait(false);
        var statusMap = participations
            .Where(p => p.Status == ParticipationStatus.NotAttending
                     || p.Status == ParticipationStatus.Ticketed
                     || p.Status == ParticipationStatus.Attended)
            .ToDictionary(p => p.UserId, p => p.Status);

        // Per-user, per-day: best status (Confirmed > Pending) plus distinct rota names.
        var perUserSignups = signups
            .GroupBy(s => s.UserId)
            .ToDictionary(g => g.Key, g => g
                .GroupBy(x => x.DayOffset)
                .ToDictionary(
                    dg => dg.Key,
                    dg => (
                        Status: dg.Any(x => x.Status == SignupStatus.Confirmed)
                            ? SignupStatus.Confirmed : SignupStatus.Pending,
                        RotaNames: (IReadOnlyList<string>)dg
                            .Select(x => x.RotaName)
                            .Distinct(StringComparer.Ordinal)
                            .ToList())));

        var bsRows = await _trackingRepo.GetByEventAsync(es.Id, ct).ConfigureAwait(false);
        var bsByUser = bsRows.ToDictionary(r => r.UserId);

        var mainRows = new List<VolunteerHeatmapRow>();
        foreach (var (userId, daySignups) in perUserSignups)
        {
            if (statusMap.TryGetValue(userId, out var st) && st == ParticipationStatus.NotAttending)
            {
                continue;
            }

            var firstSignupDay = daySignups.Keys.Min();
            var lastEligibleSignupOffset = daySignups.Keys.Max();
            bsByUser.TryGetValue(userId, out var bs);
            int? setupOffset = bs?.BarrioSetupStartDate is { } d
                ? Period.Between(es.GateOpeningDate, d, PeriodUnits.Days).Days
                : null;
            var lastExpectedDay = Math.Min(setupOffset ?? int.MaxValue, 0);
            var dayOffSet = bs?.DayOffs.Select(x => x.DayOffset).ToHashSet() ?? new HashSet<int>();

            var cells = new List<VolunteerCell>(-es.BuildStartOffset);
            int gapCount = 0;
            for (int d2 = es.BuildStartOffset; d2 < 0; d2++)
            {
                VolunteerCellState s;
                IReadOnlyList<string> rotaNames = Array.Empty<string>();
                if (setupOffset.HasValue && d2 >= setupOffset.Value)
                {
                    s = VolunteerCellState.CampSetup;
                }
                else if (d2 < firstSignupDay || d2 >= lastExpectedDay)
                {
                    s = VolunteerCellState.Outside;
                }
                else if (daySignups.TryGetValue(d2, out var info))
                {
                    s = info.Status == SignupStatus.Confirmed
                        ? VolunteerCellState.Confirmed
                        : VolunteerCellState.Pending;
                    rotaNames = info.RotaNames;
                }
                else if (dayOffSet.Contains(d2))
                {
                    s = VolunteerCellState.DayOff;
                }
                else
                {
                    s = VolunteerCellState.Gap;
                    gapCount++;
                }

                cells.Add(new VolunteerCell(d2, s, rotaNames));
            }

            var dayOffSummaries = (bs?.DayOffs ?? new List<DayOffEntry>())
                .OrderBy(x => x.DayOffset)
                .Select(x => new DayOffSummary(x.DayOffset, x.Reason))
                .ToList();

            mainRows.Add(new VolunteerHeatmapRow(
                userId,
                firstSignupDay,
                lastEligibleSignupOffset,
                bs?.BarrioSetupStartDate,
                gapCount,
                cells,
                dayOffSummaries));
        }

        var availabilityRows = await _availability.GetByEventAsync(es.Id, ct).ConfigureAwait(false);
        var availabilityByUser = availabilityRows
            .ToDictionary(g => g.UserId, g => g.AvailableDayOffsets.ToHashSet());

        var unbookedRows = new List<VolunteerCohortRow>();
        foreach (var participation in participations)
        {
            if (participation.Status != ParticipationStatus.Ticketed
                && participation.Status != ParticipationStatus.Attended)
            {
                continue;
            }

            var userId = participation.UserId;
            if (perUserSignups.ContainsKey(userId))
            {
                continue; // already in main cohort
            }

            if (!availabilityByUser.TryGetValue(userId, out var avail))
            {
                continue;
            }

            var inBuild = avail.Where(d => d >= es.BuildStartOffset && d < 0).ToHashSet();
            if (inBuild.Count == 0)
            {
                continue;
            }

            var firstAvailableDay = inBuild.Min();
            bsByUser.TryGetValue(userId, out var bs);
            int? setupOffset = bs?.BarrioSetupStartDate is { } d2
                ? Period.Between(es.GateOpeningDate, d2, PeriodUnits.Days).Days
                : null;

            var cells = new List<VolunteerCell>(-es.BuildStartOffset);
            int unbookedCount = 0;
            for (int d3 = es.BuildStartOffset; d3 < 0; d3++)
            {
                VolunteerCellState s;
                if (setupOffset.HasValue && d3 >= setupOffset.Value)
                {
                    s = VolunteerCellState.CampSetup;
                }
                else if (inBuild.Contains(d3) && d3 < todayOffset)
                {
                    s = VolunteerCellState.AvailableUnbooked;
                    unbookedCount++;
                }
                else if (inBuild.Contains(d3))
                {
                    s = VolunteerCellState.AvailableExpected;
                }
                else
                {
                    s = VolunteerCellState.NotAvailable;
                }

                cells.Add(new VolunteerCell(d3, s, Array.Empty<string>()));
            }

            unbookedRows.Add(new VolunteerCohortRow(
                userId,
                firstAvailableDay,
                bs?.BarrioSetupStartDate,
                unbookedCount,
                cells));
        }

        return new VolunteerTrackingViewModel(
            true,
            es.BuildStartOffset,
            es.GateOpeningDate,
            today,
            mainRows,
            unbookedRows);
    }

    public async Task<SetCampSetupResult> SetCampSetupAsync(
        Guid targetUserId, LocalDate barrioSetupStartDate, string? notes,
        Guid coordinatorUserId, CancellationToken ct = default)
    {
        var es = await _shiftManagement.GetActiveEventSettingsAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No active event");
        var setupOffset = Period.Between(es.GateOpeningDate, barrioSetupStartDate, PeriodUnits.Days).Days;

        if (setupOffset >= 0)
        {
            return new SetCampSetupResult(false, "VolTrack_Err_SetupAtOrAfterGateOpen", Array.Empty<int>());
        }

        var signups = await _trackingRepo.GetEligibleBuildSignupsAsync(es.Id, ct).ConfigureAwait(false);
        int? firstSignup = signups
            .Where(s => s.UserId == targetUserId)
            .Select(s => (int?)s.DayOffset)
            .DefaultIfEmpty(null)
            .Min();
        if (firstSignup.HasValue && setupOffset < firstSignup.Value)
        {
            return new SetCampSetupResult(false, "VolTrack_Err_SetupBeforeFirstSignup", Array.Empty<int>());
        }

        // Snapshot existing day-offs that the new span would shadow so we can
        // clean them up post-write. Read happens before the camp-setup write
        // because UpsertCampSetupAsync may create the row.
        var preWrite = await _trackingRepo.GetAsync(targetUserId, es.Id, ct).ConfigureAwait(false);
        var toAutoClear = preWrite is null
            ? Array.Empty<int>()
            : preWrite.DayOffs
                .Where(d => d.DayOffset >= setupOffset)
                .Select(d => d.DayOffset)
                .ToArray();

        await _trackingRepo.UpsertCampSetupAsync(
            targetUserId,
            es.Id,
            barrioSetupStartDate,
            notes,
            coordinatorUserId,
            _clock.GetCurrentInstant(),
            ct).ConfigureAwait(false);

        foreach (var d in toAutoClear)
        {
            await _trackingRepo.RemoveDayOffAsync(targetUserId, es.Id, d, ct).ConfigureAwait(false);
        }

        return new SetCampSetupResult(true, null, toAutoClear);
    }

    public async Task ClearCampSetupAsync(
        Guid targetUserId, Guid coordinatorUserId, CancellationToken ct = default)
    {
        var es = await _shiftManagement.GetActiveEventSettingsAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No active event");
        await _trackingRepo.UpsertCampSetupAsync(
            targetUserId,
            es.Id,
            barrioSetupStartDate: null,
            notes: null,
            setByUserId: null,
            setAt: null,
            ct).ConfigureAwait(false);
    }

    public async Task<SetDayOffResult> SetDayOffAsync(
        Guid targetUserId, int dayOffset, string? reason,
        Guid coordinatorUserId, CancellationToken ct = default)
    {
        var es = await _shiftManagement.GetActiveEventSettingsAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No active event");

        if (dayOffset < es.BuildStartOffset || dayOffset >= 0)
        {
            return new SetDayOffResult(false, "VolTrack_Err_DayOffOutsideBuild");
        }

        var signups = await _trackingRepo.GetEligibleBuildSignupsAsync(es.Id, ct).ConfigureAwait(false);
        var hasSignupThatDay = signups.Any(s => s.UserId == targetUserId && s.DayOffset == dayOffset);
        if (hasSignupThatDay)
        {
            return new SetDayOffResult(false, "VolTrack_Err_DayOffWithSignups");
        }

        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (trimmed is { Length: > 200 })
        {
            trimmed = trimmed[..200];
        }

        var entry = new DayOffEntry(
            DayOffset: dayOffset,
            Reason: trimmed,
            MarkedByUserId: coordinatorUserId,
            MarkedAt: _clock.GetCurrentInstant());

        await _trackingRepo.UpsertDayOffAsync(targetUserId, es.Id, entry, ct).ConfigureAwait(false);
        return new SetDayOffResult(true, null);
    }

    public async Task<ClearDayOffResult> ClearDayOffAsync(
        Guid targetUserId, int dayOffset, Guid coordinatorUserId, CancellationToken ct = default)
    {
        var es = await _shiftManagement.GetActiveEventSettingsAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No active event");

        var removed = await _trackingRepo
            .RemoveDayOffAsync(targetUserId, es.Id, dayOffset, ct)
            .ConfigureAwait(false);
        return new ClearDayOffResult(true, removed);
    }
}
