using Humans.Shifts.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Shifts.Domain;
using Humans.Settings.Contracts;
using NodaTime;
using NodaTime.Text;
using Humans.Shifts.Data;
using Humans.Shifts.Services.Dtos;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;

namespace Humans.Shifts.Services;

internal sealed class VolunteerTrackingService(
    IVolunteerTrackingRepository trackingRepo,
    IShiftManagementRepository shiftManagement,
    EventCalendarResolver calendarResolver,
    IUserService userService,
    ITicketService tickets,
    IClock clock,
    IAuditLogService audit) : IVolunteerTrackingManagement, IUserMerge
{
    private readonly VolunteerTrackingReport report = new(
        trackingRepo, shiftManagement, calendarResolver, userService, tickets, clock);

    public async Task<bool> SetAvailabilityAsync(Guid userId, Guid eventSettingsId, IReadOnlyList<int> dayOffsets)
    {
        var calendar = await calendarResolver.GetAsync(eventSettingsId);
        if (calendar is null || dayOffsets.Any(day => day < calendar.BuildStartOffset || day > calendar.StrikeEndOffset))
            return false;

        var offsets = dayOffsets.Distinct().Order().ToArray();
        var now = clock.GetCurrentInstant();
        await trackingRepo.UpsertAvailabilityAsync(userId, eventSettingsId, offsets, now);
        await audit.LogAsync(
            offsets.Length == 0 ? ShiftsAuditActions.VolunteerAvailabilityCleared : ShiftsAuditActions.VolunteerAvailabilitySet,
            "GeneralAvailability", userId,
            $"EventSettingsId={eventSettingsId}; AvailableDays={offsets.Length}; self-service update", userId,
            relatedEntityId: eventSettingsId, relatedEntityType: "EventSettings");
        return true;
    }

    public async Task<IReadOnlyList<GeneralAvailabilitySnapshot>> GetAvailableForDayAsync(
        Guid eventSettingsId, int dayOffset, CancellationToken ct = default)
    {
        // EF can't translate List<int>.Contains over jsonb, so filter in memory.
        var all = await trackingRepo.GetAvailabilityForEventAsync(eventSettingsId, ct: ct);
        return all
            .Where(g => g.AvailableDayOffsets.Contains(dayOffset))
            .Select(availability => new GeneralAvailabilitySnapshot(
                availability.UserId,
                availability.EventSettingsId,
                availability.AvailableDayOffsets))
            .ToList();
    }

    public async Task<bool> SetDayAvailabilityAsync(
        Guid userId, Guid eventSettingsId, int dayOffset, bool available,
        Guid coordinatorUserId, CancellationToken ct = default)
    {
        if (available)
        {
            var calendar = await calendarResolver.GetAsync(eventSettingsId, ct).ConfigureAwait(false);
            if (calendar is null || dayOffset < calendar.BuildStartOffset || dayOffset >= 0)
                return false;
        }

        var current = (await trackingRepo.GetAvailabilityForUserAsync(userId, eventSettingsId, ct).ConfigureAwait(false))
            .FirstOrDefault();
        var offsets = current?.AvailableDayOffsets.Distinct().ToList() ?? [];

        if (available)
        {
            if (offsets.Contains(dayOffset)) return false;
            offsets.Add(dayOffset);
        }
        else if (!offsets.Remove(dayOffset))
        {
            return false;
        }

        offsets.Sort();
        await trackingRepo.UpsertAvailabilityAsync(userId, eventSettingsId, offsets, clock.GetCurrentInstant(), ct).ConfigureAwait(false);
        await (available
            ? audit.LogAsync(
                ShiftsAuditActions.VolunteerAvailabilitySet, nameof(GeneralAvailability), userId,
                $"DayOffset={dayOffset}; marked available by coordinator", coordinatorUserId)
            : audit.LogAsync(
                ShiftsAuditActions.VolunteerAvailabilityCleared, nameof(GeneralAvailability), userId,
                $"DayOffset={dayOffset}; availability cleared by coordinator", coordinatorUserId)).ConfigureAwait(false);
        return true;
    }

    public Task<VolunteerTrackingViewModel> GetTrackingDataAsync(CancellationToken ct = default) =>
        report.GetTrackingDataAsync(ct);

    public async Task<SetCampSetupResult> SetCampSetupAsync(
        Guid targetUserId, LocalDate barrioSetupStartDate, string? notes,
        Guid coordinatorUserId, CancellationToken ct = default)
    {
        var es = await RequireActiveEventAsync(ct).ConfigureAwait(false);
        var setupOffset = Period.Between(es.GateOpeningDate, barrioSetupStartDate, PeriodUnits.Days).Days;

        if (setupOffset >= 0)
        {
            return new SetCampSetupResult(false, "Shifts_VolTrack_Err_SetupAtOrAfterGateOpen", null);
        }

        var signups = await shiftManagement.GetEligibleBuildSignupsAsync(es.Id, es.BuildStartOffset, ct).ConfigureAwait(false);
        int? firstSignup = signups
            .Where(s => s.UserId == targetUserId)
            .Select(s => (int?)s.DayOffset)
            .DefaultIfEmpty(null)
            .Min();
        if (firstSignup.HasValue && setupOffset < firstSignup.Value)
        {
            return new SetCampSetupResult(false, "Shifts_VolTrack_Err_SetupBeforeFirstSignup", null);
        }

        var trimmed = await trackingRepo.UpsertCampSetupAsync(
            targetUserId,
            es.Id,
            barrioSetupStartDate,
            notes,
            coordinatorUserId,
            clock.GetCurrentInstant(),
            setupOffsetThreshold: setupOffset,
            ct).ConfigureAwait(false);

        await audit.LogAsync(
            ShiftsAuditActions.VolunteerCampSetupSet,
            nameof(VolunteerBuildStatus),
            targetUserId,
            $"BarrioSetupStartDate set to {LocalDatePattern.Iso.Format(barrioSetupStartDate)}; notes={notes ?? "—"}",
            coordinatorUserId).ConfigureAwait(false);

        if (trimmed is { Count: > 0 })
        {
            // Audit writes through a fresh context per call, so the fan-out is safe.
            await Task.WhenAll(trimmed.Select(dayOffset =>
                audit.LogAsync(
                    ShiftsAuditActions.VolunteerDayOffCleared,
                    nameof(VolunteerBuildStatus),
                    targetUserId,
                    $"DayOffset={dayOffset}; auto-cleared by camp-setup change",
                    coordinatorUserId))).ConfigureAwait(false);
        }

        return new SetCampSetupResult(true, null, trimmed);
    }

    public async Task ClearCampSetupAsync(
        Guid targetUserId, Guid coordinatorUserId, CancellationToken ct = default)
    {
        var es = await RequireActiveEventAsync(ct).ConfigureAwait(false);
        await trackingRepo.UpsertCampSetupAsync(
            targetUserId,
            es.Id,
            barrioSetupStartDate: null,
            notes: null,
            setByUserId: null,
            setAt: null,
            setupOffsetThreshold: null,
            ct).ConfigureAwait(false);
        await audit.LogAsync(
            ShiftsAuditActions.VolunteerCampSetupCleared,
            nameof(VolunteerBuildStatus),
            targetUserId,
            "BarrioSetupStartDate cleared",
            coordinatorUserId).ConfigureAwait(false);
    }

    public async Task<SetDayOffResult> SetDayOffAsync(
        Guid targetUserId, int dayOffset, string? reason,
        Guid coordinatorUserId, CancellationToken ct = default)
    {
        var es = await RequireActiveEventAsync(ct).ConfigureAwait(false);

        if (dayOffset < es.BuildStartOffset || dayOffset >= 0)
        {
            return new SetDayOffResult(false, "Shifts_VolTrack_Err_DayOffOutsideBuild");
        }

        var signups = await shiftManagement.GetEligibleBuildSignupsAsync(es.Id, es.BuildStartOffset, ct).ConfigureAwait(false);
        var hasSignupThatDay = signups.Any(s => s.UserId == targetUserId && s.DayOffset == dayOffset);
        if (hasSignupThatDay)
        {
            return new SetDayOffResult(false, "Shifts_VolTrack_Err_DayOffWithSignups");
        }

        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (trimmed is { Length: > 200 })
        {
            var length = char.IsHighSurrogate(trimmed[199]) && char.IsLowSurrogate(trimmed[200]) ? 199 : 200;
            trimmed = trimmed[..length];
        }

        var entry = new DayOffEntry(
            DayOffset: dayOffset,
            Reason: trimmed,
            MarkedByUserId: coordinatorUserId,
            MarkedAt: clock.GetCurrentInstant());

        await trackingRepo.UpsertDayOffAsync(targetUserId, es.Id, entry, ct).ConfigureAwait(false);
        await audit.LogAsync(
            ShiftsAuditActions.VolunteerDayOffMarked,
            nameof(VolunteerBuildStatus),
            targetUserId,
            $"DayOffset={dayOffset}; reason={(string.IsNullOrWhiteSpace(reason) ? "—" : reason)}",
            coordinatorUserId).ConfigureAwait(false);
        return new SetDayOffResult(true, null);
    }

    public async Task<ClearDayOffResult> ClearDayOffAsync(
        Guid targetUserId, int dayOffset, Guid coordinatorUserId, CancellationToken ct = default)
    {
        var es = await RequireActiveEventAsync(ct).ConfigureAwait(false);

        var removed = await trackingRepo.RemoveDayOffAsync(targetUserId, es.Id, dayOffset, ct).ConfigureAwait(false);
        if (removed)
        {
            await audit.LogAsync(
                ShiftsAuditActions.VolunteerDayOffCleared,
                nameof(VolunteerBuildStatus),
                targetUserId,
                $"DayOffset={dayOffset}; cleared by coordinator",
                coordinatorUserId).ConfigureAwait(false);
        }
        return new ClearDayOffResult(removed);
    }

    public Task<VolunteerBuildStripDto?> GetUserBuildStripAsync(Guid userId, CancellationToken ct = default) =>
        report.GetUserBuildStripAsync(userId, ct);

    public async Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken ct)
    {
        await trackingRepo.ReassignAvailabilityToUserAsync(sourceUserId, targetUserId, updatedAt, ct);
    }

    private async Task<BurnSettingsInfo> RequireActiveEventAsync(CancellationToken ct) =>
        await calendarResolver.GetActiveAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No active event");
}
