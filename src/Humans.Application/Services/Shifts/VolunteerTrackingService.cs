using Humans.Application.DTOs;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
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
            var blockedSet = bs?.BlockedDayOffsets.ToHashSet() ?? new HashSet<int>();
            var lastExpectedDay = Math.Min(
                Math.Min(setupOffset ?? int.MaxValue, 0),
                todayOffset + 1);

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
                else if (blockedSet.Contains(d2))
                {
                    s = VolunteerCellState.Blocked;
                }
                else if (daySignups.TryGetValue(d2, out var info))
                {
                    s = info.Status == SignupStatus.Confirmed
                        ? VolunteerCellState.Confirmed
                        : VolunteerCellState.Pending;
                    rotaNames = info.RotaNames;
                }
                else if (d2 < todayOffset)
                {
                    s = VolunteerCellState.Gap;
                    gapCount++;
                }
                else
                {
                    s = VolunteerCellState.Expected;
                }

                cells.Add(new VolunteerCell(d2, s, rotaNames));
            }

            mainRows.Add(new VolunteerHeatmapRow(
                userId,
                firstSignupDay,
                lastEligibleSignupOffset,
                bs?.BarrioSetupStartDate,
                gapCount,
                cells));
        }

        return new VolunteerTrackingViewModel(
            true,
            es.BuildStartOffset,
            mainRows,
            Array.Empty<VolunteerCohortRow>());
    }

    public Task<SetCampSetupResult> SetCampSetupAsync(
        Guid targetUserId, LocalDate barrioSetupStartDate, string? notes,
        Guid coordinatorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Not yet implemented.");

    public Task ClearCampSetupAsync(
        Guid targetUserId, Guid coordinatorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Not yet implemented.");

    public Task<SetBlockResult> SetBlockAsync(
        Guid targetUserId, int dayOffset, bool block,
        Guid coordinatorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Not yet implemented.");

    public Task<SaveOwnBlockedDaysResult> SaveOwnBlockedDaysAsync(
        Guid ownerUserId, IReadOnlyList<int> dayOffsets, CancellationToken ct = default)
        => throw new NotSupportedException("Not yet implemented.");

    public Task<MineBlockedDaysSummary> GetMineBlockedDaysSummaryAsync(
        Guid userId, CancellationToken ct = default)
        => throw new NotSupportedException("Not yet implemented.");
}
