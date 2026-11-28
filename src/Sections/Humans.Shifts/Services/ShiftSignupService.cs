using System.Globalization;
using System.Resources;
using Humans.Base.Extensions;
using Humans.Base.Threading;
using Humans.Audit.Contracts;
using Humans.Calendar.Contracts;
using Humans.Notifications.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Shifts.Domain;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Humans.Shifts.Data;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using Humans.Settings.Contracts;
using Microsoft.Extensions.Localization;

namespace Humans.Shifts.Services;

/// <summary>Keyed inner of <see cref="CachingShiftService"/>.</summary>
internal sealed class ShiftSignupService(
    IShiftManagementRepository repo,
    IVolunteerTrackingRepository trackingRepo,
    IShiftManagement shiftMgmt,
    EventCalendarResolver calendarResolver,
    IAuditLogService auditLogService,
    INotificationEmitter notificationService,
    IServiceProvider serviceProvider,
    IClock clock,
    ILogger<ShiftSignupService> logger,
    IUserService users,
    IStringLocalizer<ShiftsResource> localizer) : IShiftSignupService, IUserMerge, ICalendarFeedContributor
{
    // Released after the enrollment save, before audit and notices; refusals and failures release via using.
    private static readonly TrackedLock EnrollmentLock = new("Shifts.Enrollment");

    /// <summary>GDPR export key.</summary>
    internal const string ShiftSignups = "ShiftSignups";
    internal const string VolunteerEventProfiles = "VolunteerEventProfiles";
    internal const string GeneralAvailability = "GeneralAvailability";
    internal const string VolunteerBuildStatuses = "VolunteerBuildStatuses";
    internal const string ShiftTagPreferences = "ShiftTagPreferences";

    public Task<SignupResult> SignUpAsync(
        Guid userId,
        Guid shiftId,
        Guid? actorUserId = null,
        ShiftSignupRequestFlags flags = ShiftSignupRequestFlags.None) =>
        EnrollAsync(userId, shiftId, new Enrollment(actorUserId, flags));

    public Task<SignupResult> VoluntellAsync(Guid userId, Guid shiftId, Guid enrollerUserId) =>
        EnrollAsync(userId, shiftId, Enrollment.Voluntell(enrollerUserId));

    public Task<SignupResult> SignUpRangeAsync(
        Guid userId,
        Guid rotaId,
        int startDayOffset,
        int endDayOffset,
        Guid? actorUserId = null,
        ShiftSignupRequestFlags flags = ShiftSignupRequestFlags.None) =>
        EnrollRangeAsync(userId, rotaId, startDayOffset, endDayOffset, new Enrollment(actorUserId, flags));

    public Task<SignupResult> VoluntellRangeAsync(Guid userId, Guid rotaId, int startDayOffset, int endDayOffset, Guid enrollerUserId) =>
        EnrollRangeAsync(userId, rotaId, startDayOffset, endDayOffset, Enrollment.Voluntell(enrollerUserId));

    /// <summary>A volunteer's own signup, or a coordinator's voluntell: privileged, auto-confirmed, conflicts skipped.</summary>
    private sealed record Enrollment(Guid? ActorUserId, ShiftSignupRequestFlags Flags, bool Voluntold = false)
    {
        public static Enrollment Voluntell(Guid enrollerUserId) => new(
            enrollerUserId, ShiftSignupRequestFlags.Privileged | ShiftSignupRequestFlags.SkipConflicts, Voluntold: true);
    }

    // Voluntell may target any live account; a volunteer's own signup needs an active one.
    private async Task<Guid?> ResolveEnrolleeAsync(Guid userId, Enrollment enrollment)
    {
        var user = await users.GetUserInfoAsync(userId);
        var eligible = enrollment.Voluntold ? user is { IsTombstone: false } : user?.State == UserState.Active;
        return eligible ? user!.Id : null;
    }

    private async Task<SignupResult> EnrollAsync(Guid userId, Guid shiftId, Enrollment enrollment)
    {
        Shift? shift;
        BurnSettingsInfo? calendar;
        Instant now;
        string? warning;
        bool autoConfirm;
        ShiftSignup signup;
        using (await EnrollmentLock.AcquireAsync(logger))
        {
            if (await ResolveEnrolleeAsync(userId, enrollment) is not { } enrolleeId)
                return SignupResult.Fail(localizer["Shifts_AccountCannotSignUp"]);
            userId = enrolleeId;

            var activeShiftIds = await repo.GetActiveShiftIdsForUserAsync(userId, [shiftId]);
            if (activeShiftIds.Contains(shiftId))
                return SignupResult.Fail(localizer["Shifts_Signup_AlreadySignedUp"]);

            shift = await repo.GetShiftAsync(shiftId, ShiftReadShape.Context);
            if (shift is null) return SignupResult.Fail(localizer["Shifts_Signup_ShiftNotFound"]);

            calendar = await calendarResolver.GetAsync(shift.Rota.EventSettingsId);
            if (calendar is null) return SignupResult.Fail(localizer["Shifts_EventCalendarNotConfigured"]);
            now = clock.GetCurrentInstant();
            var (isPrivileged, canApprove) = await ResolvePrivilegeAsync(userId, shift.Rota.TeamId, enrollment);

            if (!shift.Rota.EventSettings.IsShiftBrowsingOpen && !isPrivileged)
                return SignupResult.Fail(localizer["Shifts_Signup_BrowsingClosed"]);

            if ((!shift.Rota.IsVisibleToVolunteers || shift.AdminOnly) && !isPrivileged)
                return SignupResult.Fail(localizer["Shifts_Signup_Restricted"]);

            if (shift.IsEarlyEntry && calendar.IsEarlyEntryClosed(now) && !isPrivileged)
                return SignupResult.Fail(localizer["Shifts_Signup_EarlyEntryClosed"]);

            var overlapWarning = await CheckOverlapAsync(userId, shift, calendar);
            if (overlapWarning is not null)
                return SignupResult.Fail(overlapWarning);

            var confirmedCount = shift.ShiftSignups.Count(d => d.Status == SignupStatus.Confirmed);
            if (confirmedCount >= shift.MaxVolunteers)
                return SignupResult.Fail(localizer["Shifts_Signup_AtCapacity"]);

            warning = shift.IsEarlyEntry ? await CheckEeCapAsync(calendar, shift.DayOffset) : null;

            autoConfirm = await IsAutoConfirmedAsync(userId, shift.Rota, enrollment, canApprove);
            signup = StageSignup(userId, shift.Id, enrollment, now, autoConfirm, blockId: null);

            await repo.SaveChangesAsync();
        }
        await AuditEnrollmentAsync(userId, shift.Rota, calendar, [(signup, shift.DayOffset)], enrollment, autoConfirm, range: false);

        if (enrollment.Voluntold)
        {
            await DispatchSignupChangeNotificationAsync(signup, shift,
                "Shifts_Notification_ChangeAssigned", shift.Rota.Name, shift.DayOffset);
            if (shift.GetAbsoluteEnd(calendar) > now)
                await SendAssignmentNoticeAsync(userId, signup.Id, "Shifts_Notification_AssignedDay", shift.Rota.Name, shift.DayOffset);
        }
        else if (autoConfirm)
        {
            await DispatchSignupChangeNotificationAsync(signup, shift,
                "Shifts_Notification_ChangeConfirmed", shift.Rota.Name, shift.DayOffset);
        }

        return SignupResult.Ok(signup.Id, warning);
    }

    private async Task<SignupResult> EnrollRangeAsync(
        Guid userId, Guid rotaId, int startDayOffset, int endDayOffset, Enrollment enrollment)
    {
        Rota? rota;
        BurnSettingsInfo? calendar;
        Instant now;
        string? warning;
        List<Shift> availableShifts;
        bool autoConfirm;
        List<(ShiftSignup Signup, int DayOffset)> created;
        using (await EnrollmentLock.AcquireAsync(logger))
        {
            if (await ResolveEnrolleeAsync(userId, enrollment) is not { } enrolleeId)
                return SignupResult.Fail(localizer["Shifts_AccountCannotSignUp"]);
            userId = enrolleeId;

            var skipConflicts = enrollment.Flags.HasFlag(ShiftSignupRequestFlags.SkipConflicts);
            rota = await repo.GetRotaAsync(rotaId, RotaReadShape.EventSettings | RotaReadShape.Shifts);
            if (rota is null) return SignupResult.Fail(localizer["Shifts_Signup_RotaNotFound"]);

            calendar = await calendarResolver.GetAsync(rota.EventSettingsId);
            if (calendar is null) return SignupResult.Fail(localizer["Shifts_EventCalendarNotConfigured"]);
            now = clock.GetCurrentInstant();
            var (isPrivileged, canApprove) = await ResolvePrivilegeAsync(userId, rota.TeamId, enrollment);

            if (!rota.EventSettings.IsShiftBrowsingOpen && !isPrivileged)
                return SignupResult.Fail(localizer["Shifts_Signup_BrowsingClosed"]);

            if (!rota.IsVisibleToVolunteers && !isPrivileged)
                return SignupResult.Fail(localizer["Shifts_Signup_RangeRestricted"]);

            if (rota.Period == RotaPeriod.Build && calendar.IsEarlyEntryClosed(now) && !isPrivileged)
                return SignupResult.Fail(localizer["Shifts_Signup_EarlyEntryClosed"]);

            var shiftsInRange = SelectAllDayRangeShifts(rota, startDayOffset, endDayOffset);

            if (!isPrivileged && shiftsInRange.Any(s => s.AdminOnly))
                return SignupResult.Fail(localizer["Shifts_Signup_RangeRestricted"]);

            var existingSignups = await GetActiveUserSignupsAsync(userId);

            var duplicateSelection = PruneDuplicateRangeShifts(shiftsInRange, existingSignups, calendar, skipConflicts);
            if (duplicateSelection.Failure is not null)
                return duplicateSelection.Failure;

            var conflictSelection = await PruneConflictingRangeShiftsAsync(
                duplicateSelection.Shifts,
                existingSignups,
                calendar,
                duplicateSelection.Warnings,
                skipConflicts);
            if (conflictSelection.Failure is not null)
                return conflictSelection.Failure;

            shiftsInRange = conflictSelection.Shifts;
            if (shiftsInRange.Count == 0)
                return conflictSelection.Warnings.Count > 0
                    ? SignupResult.Fail(string.Join(" ", conflictSelection.Warnings) + " " + localizer["Shifts_Signup_RangeNothingToAdd"].Value)
                    : SignupResult.Fail(localizer["Shifts_Signup_RangeEmpty"]);

            var capacitySelection = await SelectCapacityAvailableRangeShiftsAsync(shiftsInRange);
            if (capacitySelection.AvailableShifts.Count == 0)
                return SignupResult.Fail(localizer["Shifts_Signup_RangeAtCapacity"]);

            warning = conflictSelection.Warnings.Count > 0
                ? string.Join(" ", conflictSelection.Warnings)
                : null;
            if (capacitySelection.FullDayOffsets.Count > 0)
            {
                var dayList = FormatRangeDayList(calendar, capacitySelection.FullDayOffsets);
                warning = AppendRangeWarning(warning, localizer["Shifts_Signup_RangeCapacityDays", dayList].Value);
            }

            availableShifts = capacitySelection.AvailableShifts;

            if (rota.Period == RotaPeriod.Build)
                warning = await AppendEarlyEntryRangeWarningAsync(warning, availableShifts, calendar);

            var blockId = Guid.NewGuid();
            autoConfirm = await IsAutoConfirmedAsync(userId, rota, enrollment, canApprove);
            created = availableShifts
                .Select(shift => (Signup: StageSignup(userId, shift.Id, enrollment, now, autoConfirm, blockId), shift.DayOffset))
                .ToList();

            await repo.SaveChangesAsync();
        }
        await AuditEnrollmentAsync(userId, rota, calendar, created, enrollment, autoConfirm, range: true);

        var lastSignup = created[^1].Signup;
        if (enrollment.Voluntold)
        {
            await DispatchSignupChangeNotificationAsync(lastSignup, availableShifts[^1], rota,
                "Shifts_Notification_ChangeAssignedRange", rota.Name, availableShifts.Count);
            if (availableShifts.Any(s => s.GetAbsoluteEnd(calendar) > now))
            {
                await SendAssignmentNoticeAsync(userId, lastSignup.Id, availableShifts.Count == 1
                    ? "Shifts_Notification_AssignedRangeSingular" : "Shifts_Notification_AssignedRange", rota.Name, availableShifts.Count);
            }
        }
        else if (autoConfirm)
        {
            await DispatchSignupChangeNotificationAsync(lastSignup, availableShifts[^1], rota,
                "Shifts_Notification_ChangeConfirmedRange", rota.Name, availableShifts.Count);
        }

        return SignupResult.Ok(lastSignup.Id, warning);
    }

    // canApprove is the volunteer's own approval right, looked up once and reused for auto-confirm.
    private async Task<(bool IsPrivileged, bool? CanApprove)> ResolvePrivilegeAsync(
        Guid userId, Guid teamId, Enrollment enrollment)
    {
        if (enrollment.Flags.HasFlag(ShiftSignupRequestFlags.Privileged))
            return (true, null);
        var canApprove = await IsPrivilegedAsync(userId, teamId);
        return (canApprove, canApprove);
    }

    // Public rotas auto-confirm whatever the admission or consent status; only RequireApproval parks as Pending.
    private async Task<bool> IsAutoConfirmedAsync(Guid userId, Rota rota, Enrollment enrollment, bool? canApprove) =>
        enrollment.Voluntold
        || rota.Policy == SignupPolicy.Public
        || (canApprove ?? await IsPrivilegedAsync(userId, rota.TeamId));

    private ShiftSignup StageSignup(Guid userId, Guid shiftId, Enrollment enrollment, Instant now, bool autoConfirm, Guid? blockId)
    {
        var signup = NewSignup(userId, shiftId, enrollment.ActorUserId, now, autoConfirm, blockId);
        if (enrollment.Voluntold)
        {
            signup.Enrolled = true;
            signup.EnrolledByUserId = enrollment.ActorUserId;
        }

        repo.AddRange([signup]);
        return signup;
    }

    private async Task AuditEnrollmentAsync(
        Guid userId,
        Rota rota,
        BurnSettingsInfo calendar,
        IEnumerable<(ShiftSignup Signup, int DayOffset)> created,
        Enrollment enrollment,
        bool autoConfirm,
        bool range)
    {
        var statusSuffix = autoConfirm ? "confirmed" : "pending";
        foreach (var (signup, dayOffset) in created)
        {
            var on = $"'{rota.Name}' on {FormatAuditDay(calendar, dayOffset)}";
            var (action, description) = enrollment.Voluntold
                ? (ShiftsAuditActions.ShiftSignupVoluntold, range ? $"{on} (range)" : $"shift {on}")
                : (ShiftsAuditActions.ShiftSignupCreated, range ? $"{on} (range, {statusSuffix})" : $"shift {on} ({statusSuffix})");
            await auditLogService.LogAsync(
                action, nameof(ShiftSignup), signup.Id, description,
                enrollment.ActorUserId ?? userId,
                userId, AuditSubjectTypes.User);
        }
    }

    // Approve, refuse and bail act on a group: a range's block, or one signup as a group of one.

    public async Task<SignupResult> ApproveAsync(Guid signupId, Guid reviewerUserId)
    {
        using var enrollmentLock = await EnrollmentLock.AcquireAsync(logger);
        var signup = await repo.GetByIdForMutationAsync(signupId);
        if (signup is null) return SignupResult.Fail(localizer["Shifts_SignupNotFound"]);
        if (signup.Status != SignupStatus.Pending)
            return SignupResult.Fail($"Cannot approve signup in {signup.Status} state.");
        return await ApproveGroupAsync([signup], reviewerUserId, enrollmentLock);
    }

    public async Task<SignupResult> ApproveRangeAsync(Guid signupBlockId, Guid reviewerUserId)
    {
        using var enrollmentLock = await EnrollmentLock.AcquireAsync(logger);
        var signups = await repo.GetBlockForMutationAsync(signupBlockId, ShiftSignupBlockMutationScope.PendingOnly);
        if (signups.Count == 0) return SignupResult.Fail("No pending signups found for this block.");
        return await ApproveGroupAsync(signups, reviewerUserId, enrollmentLock);
    }

    public async Task<SignupResult> RefuseAsync(Guid signupId, Guid reviewerUserId, string? reason)
    {
        var signup = await repo.GetByIdForMutationAsync(signupId);
        if (signup is null) return SignupResult.Fail(localizer["Shifts_SignupNotFound"]);
        return await RefuseGroupAsync([signup], reviewerUserId, reason);
    }

    public async Task<SignupResult> RefuseRangeAsync(Guid signupBlockId, Guid reviewerUserId, string? reason)
    {
        var signups = await repo.GetBlockForMutationAsync(signupBlockId, ShiftSignupBlockMutationScope.PendingOnly);
        if (signups.Count == 0) return SignupResult.Fail("No pending signups found for this block.");
        return await RefuseGroupAsync(signups, reviewerUserId, reason);
    }

    public async Task<SignupResult> BailAsync(Guid signupId, Guid actorUserId, string? reason)
    {
        var signup = await repo.GetByIdForMutationAsync(signupId);
        if (signup is null) return SignupResult.Fail(localizer["Shifts_SignupNotFound"]);
        return await BailGroupAsync([signup], actorUserId, reason, "Shifts_Bail_NotAuthorized") is { } errorKey
            ? SignupResult.Fail(localizer[errorKey])
            : SignupResult.Ok(signup.Id);
    }

    public async Task<BailRangeResult> BailRangeAsync(Guid signupBlockId, Guid actorUserId, string? reason = null)
    {
        var signups = await repo.GetBlockForMutationAsync(signupBlockId, ShiftSignupBlockMutationScope.PendingAndConfirmed);
        if (signups.Count == 0) return new();
        return new(await BailGroupAsync(signups, actorUserId, reason, "Shifts_BailRange_NotAuthorized"));
    }

    // A full shift is refused, not left pending, so the rest of the group can still be approved.
    private async Task<SignupResult> ApproveGroupAsync(List<ShiftSignup> group, Guid reviewerUserId, TrackedLock.Releaser enrollmentLock)
    {
        var calendar = await calendarResolver.GetAsync(group[0].Shift.Rota.EventSettingsId);
        if (calendar is null) return SignupResult.Fail(localizer["Shifts_EventCalendarNotConfigured"]);

        var warnings = new List<string>();
        var now = clock.GetCurrentInstant();
        var existingSignups = await GetActiveUserSignupsAsync(group[0].UserId);
        var conflictingDays = await GetConflictingRangeDaysAsync(
            group.Select(s => s.Shift).ToList(), existingSignups, calendar);
        if (conflictingDays.Count > 0)
            warnings.Add(localizer["Shifts_Signup_RangeConflictDays", FormatRangeDayList(calendar, conflictingDays)].Value);

        var approved = new List<ShiftSignup>();
        var full = new List<ShiftSignup>();
        foreach (var signup in group)
        {
            if (signup.Shift.ShiftSignups.Count(d => d.Status == SignupStatus.Confirmed) >= signup.Shift.MaxVolunteers)
            {
                full.Add(signup);
                continue;
            }

            if (signup.Shift.IsEarlyEntry)
            {
                if (await CheckEeCapAsync(calendar, signup.Shift.DayOffset) is { } eeWarning)
                    warnings.Add(eeWarning);
                if (calendar.IsEarlyEntryClosed(now) && !await IsPrivilegedAsync(reviewerUserId, signup.Shift.Rota.TeamId))
                    return SignupResult.Fail("Cannot approve build shift signups after early entry close.");
            }

            approved.Add(signup);
        }

        foreach (var signup in approved)
            signup.Confirm(reviewerUserId, clock);
        foreach (var signup in full)
            signup.Refuse(reviewerUserId, clock, "Shift at capacity");
        if (full.Count > 0)
            warnings.Add($"Day(s) {string.Join(", ", full.Select(s => s.Shift.DayOffset))} refused (at capacity).");

        await repo.SaveChangesAsync();
        enrollmentLock.Dispose();

        foreach (var signup in approved)
            await AuditGroupMemberAsync(ShiftsAuditActions.ShiftSignupConfirmed, group, signup, reviewerUserId, reason: null);
        foreach (var signup in full)
        {
            await auditLogService.LogAsync(
                ShiftsAuditActions.ShiftSignupRefused, nameof(ShiftSignup), signup.Id,
                $"shift '{signup.Shift.Rota.Name}' day {signup.Shift.DayOffset} (auto-refused, at capacity)",
                reviewerUserId,
                signup.UserId, AuditSubjectTypes.User);
        }

        if (approved.Count == 0)
            return SignupResult.Fail(group.Count == 1
                ? "Cannot approve: shift is at capacity, so the request was refused."
                : "Cannot approve: all shifts in this range are at capacity, so the requests were refused.");

        await NotifyGroupChangeAsync(approved, "Shifts_Notification_ChangeApproved");

        var warning = warnings.Count > 0 ? string.Join(" ", warnings.Distinct(StringComparer.Ordinal)) : null;
        return SignupResult.Ok(approved[0].Id, warning);
    }

    private async Task<SignupResult> RefuseGroupAsync(List<ShiftSignup> group, Guid reviewerUserId, string? reason)
    {
        if (IsStatusReasonTooLong(reason, group[0].Id, reviewerUserId))
            return SignupResult.Fail(localizer["Shifts_StatusReasonTooLong"]);

        foreach (var signup in group)
            signup.Refuse(reviewerUserId, clock, reason);

        await repo.SaveChangesAsync();

        foreach (var signup in group)
            await AuditGroupMemberAsync(ShiftsAuditActions.ShiftSignupRefused, group, signup, reviewerUserId, reason);

        await NotifyGroupChangeAsync(group, "Shifts_Notification_ChangeRefused");
        return SignupResult.Ok(group[0].Id);
    }

    // Returns the refusal's resource key, or null once bailed.
    private async Task<string?> BailGroupAsync(List<ShiftSignup> group, Guid actorUserId, string? reason, string notAuthorizedKey)
    {
        var first = group[0];
        var calendar = await calendarResolver.GetAsync(first.Shift.Rota.EventSettingsId);
        if (calendar is null) return "Shifts_EventCalendarNotConfigured";
        var isOwner = first.UserId == actorUserId;
        var earlyEntryClosed = group.Any(s => s.Shift.IsEarlyEntry) && calendar.IsEarlyEntryClosed(clock.GetCurrentInstant());
        var isPrivileged = (!isOwner || earlyEntryClosed)
            && await IsPrivilegedAsync(actorUserId, first.Shift.Rota.TeamId);

        if (!isOwner && !isPrivileged)
            return notAuthorizedKey;

        // A block is loaded active-only, so these refusals can only meet a single signup.
        if (first.Status is not (SignupStatus.Pending or SignupStatus.Confirmed))
        {
            logger.LogWarning("Bail attempted on inactive signup {SignupId} in {Status} state by actor {ActorUserId}",
                first.Id, first.Status, actorUserId);
            return first.Status == SignupStatus.Bailed ? "Shifts_Bail_AlreadyBailed" : "Shifts_Bail_NotActive";
        }

        if (earlyEntryClosed && !isPrivileged)
            return "Shifts_Bail_EarlyEntryClosed";

        if (IsStatusReasonTooLong(reason, first.Id, actorUserId))
            return "Shifts_StatusReasonTooLong";

        // Captured before Bail gives Pending and Confirmed the same status: only confirmed ones reduce staffing.
        var confirmed = group.Where(s => s.Status == SignupStatus.Confirmed).ToList();
        foreach (var signup in group)
            signup.Bail(actorUserId, clock, reason);

        await repo.SaveChangesAsync();

        foreach (var signup in group)
            await AuditGroupMemberAsync(ShiftsAuditActions.ShiftSignupBailed, group, signup, actorUserId, reason);

        await NotifyGroupChangeAsync(group, "Shifts_Notification_ChangeBailed");

        foreach (var signup in confirmed)
            await NotifyCoverageGapAsync(signup, signup.Shift);
        return null;
    }

    private Task AuditGroupMemberAsync(AuditActionDefinition action, List<ShiftSignup> group, ShiftSignup signup, Guid actorUserId, string? reason) =>
        auditLogService.LogAsync(
            action, nameof(ShiftSignup), signup.Id,
            $"shift '{signup.Shift.Rota.Name}'"
                + (group.Count > 1 ? $" day {signup.Shift.DayOffset} (range)" : "")
                + (reason is not null ? $": {reason}" : ""),
            actorUserId,
            signup.UserId, AuditSubjectTypes.User);

    // A single change names its day; a range names its size, under the key's Range variant.
    private Task NotifyGroupChangeAsync(List<ShiftSignup> changed, string resourceKey)
    {
        var first = changed[0];
        return changed.Count == 1
            ? DispatchSignupChangeNotificationAsync(first, first.Shift, resourceKey, first.Shift.Rota.Name, first.Shift.DayOffset)
            : DispatchSignupChangeNotificationAsync(first, first.Shift, resourceKey + "Range", first.Shift.Rota.Name, changed.Count);
    }

    public async Task<SignupResult> MarkNoShowAsync(Guid signupId, Guid reviewerUserId)
    {
        var signup = await repo.GetByIdForMutationAsync(signupId);
        if (signup is null) return SignupResult.Fail(localizer["Shifts_SignupNotFound"]);

        var calendar = await calendarResolver.GetAsync(signup.Shift.Rota.EventSettingsId);
        if (calendar is null) return SignupResult.Fail(localizer["Shifts_EventCalendarNotConfigured"]);
        var shiftEnd = signup.Shift.GetAbsoluteEnd(calendar);
        var now = clock.GetCurrentInstant();

        if (now < shiftEnd)
            return SignupResult.Fail("Cannot mark no-show before the shift ends.");

        if (signup.Status != SignupStatus.Confirmed)
            return SignupResult.Fail($"Cannot mark no-show for a signup in {signup.Status} state.");

        signup.MarkNoShow(reviewerUserId, clock);

        await repo.SaveChangesAsync();

        await auditLogService.LogAsync(
            ShiftsAuditActions.ShiftSignupNoShow, nameof(ShiftSignup), signup.Id,
            $"shift '{signup.Shift.Rota.Name}'",
            reviewerUserId,
            signup.UserId, AuditSubjectTypes.User);

        return SignupResult.Ok(signup.Id);
    }

    public async Task<SignupResult> RemoveSignupAsync(Guid signupId, Guid removedByUserId, string? reason)
    {
        var signup = await repo.GetByIdForMutationAsync(signupId);
        if (signup is null) return SignupResult.Fail(localizer["Shifts_SignupNotFound"]);

        if (signup.Status != SignupStatus.Confirmed)
            return SignupResult.Fail($"Cannot remove signup in {signup.Status} state.");

        if (IsStatusReasonTooLong(reason, signup.Id, removedByUserId))
            return SignupResult.Fail(localizer["Shifts_StatusReasonTooLong"]);

        signup.Remove(removedByUserId, clock, reason);

        await repo.SaveChangesAsync();

        await auditLogService.LogAsync(
            ShiftsAuditActions.ShiftSignupCancelled, nameof(ShiftSignup), signup.Id,
            $"shift '{signup.Shift.Rota.Name}'" +
            (reason is not null ? $": {reason}" : ""),
            removedByUserId,
            signup.UserId, AuditSubjectTypes.User);

        await DispatchSignupChangeNotificationAsync(signup, signup.Shift,
            "Shifts_Notification_ChangeRemoved", signup.Shift.Rota.Name, signup.Shift.DayOffset);
        await NotifyCoverageGapAsync(signup, signup.Shift);

        return SignupResult.Ok(signup.Id);
    }

    private async Task<string?> AppendEarlyEntryRangeWarningAsync(
        string? warning,
        IReadOnlyList<Shift> availableShifts,
        BurnSettingsInfo eventSettings)
    {
        var fullEeDays = new List<int>();
        foreach (var dayOffset in availableShifts
                     .Where(shift => shift.IsEarlyEntry)
                     .Select(shift => shift.DayOffset)
                     .Distinct()
                     .OrderBy(day => day))
        {
            var eeWarning = await CheckEeCapAsync(eventSettings, dayOffset);
            if (eeWarning is not null)
                fullEeDays.Add(dayOffset);
        }

        if (fullEeDays.Count == 0)
            return warning;

        var eeDayList = FormatRangeDayList(eventSettings, fullEeDays);
        return AppendRangeWarning(warning, localizer["Shifts_Signup_RangeEarlyEntryCapacityDays", eeDayList].Value);
    }

    private static string AppendRangeWarning(string? warning, string nextWarning)
        => warning is null ? nextWarning : $"{warning} {nextWarning}";

    // Audit and notice text is persisted English; keep its dates out of the actor's UI culture.
    private static string FormatAuditDay(BurnSettingsInfo eventSettings, int dayOffset)
    {
        using var culture = new CultureScope("en");
        return eventSettings.GateOpeningDate.PlusDays(dayOffset).ToWeekdayDayMonth();
    }

    private Task<bool> IsPrivilegedAsync(Guid userId, Guid teamId) =>
        shiftMgmt.CanApproveSignupsAsync(userId, teamId);

    private static ShiftSignup NewSignup(
        Guid userId, Guid shiftId, Guid? actorUserId, Instant now, bool autoConfirm, Guid? blockId = null) => new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ShiftId = shiftId,
            SignupBlockId = blockId,
            Status = autoConfirm ? SignupStatus.Confirmed : SignupStatus.Pending,
            ReviewedByUserId = autoConfirm ? actorUserId ?? userId : null,
            ReviewedAt = autoConfirm ? now : null,
            CreatedAt = now,
            UpdatedAt = now
        };

    public async Task<ShiftSignupTeamProbe?> GetTeamProbeAsync(Guid id, ShiftSignupTeamProbeScope scope, CancellationToken ct = default) =>
        (await serviceProvider.GetRequiredService<IShiftRotaRows>().GetRotaRowsAsync(ct: ct))
            .SelectMany(r => r.Shifts.SelectMany(s => s.Signups
                .Where(signup => scope == ShiftSignupTeamProbeScope.Signup ? signup.Id == id : signup.SignupBlockId == id)
                .Select(signup => new ShiftSignupTeamProbe(r.Rota.TeamId))))
            .FirstOrDefault();

    private bool IsStatusReasonTooLong(string? reason, Guid signupId, Guid actorUserId)
    {
        if (reason is not { Length: > 1000 }) return false;
        logger.LogWarning("Rejected status reason longer than 1000 characters for signup {SignupId} by actor {ActorId}",
            signupId, actorUserId);
        return true;
    }

    internal static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ShiftSignups] =
                "Partially retained: the signup row (shift, status, timestamps, keyed to the user " +
                "id the Users section tombstones) is the association's record of who covered " +
                "which volunteer shift — Ley Orgánica 1/2002 Art. 14, GDPR Art. 17(3)(b). Every " +
                "still-active signup is cancelled immediately, and the free-text status reason " +
                "a coordinator wrote on any signup is cleared.",
            [VolunteerEventProfiles] = null,
            [GeneralAvailability] = null,
            [VolunteerBuildStatuses] = null,
            [ShiftTagPreferences] = null
        };

    /// <summary>Cancels live commitments, then deletes the profile, availability, build-status and tag rows.</summary>
    public async Task EraseForUserAsync(Guid userId, CancellationToken ct)
    {
        // Before cancelling, so the "Account deletion" reason survives: the kept row is shift, status and
        // timestamps, not what a coordinator typed about the person.
        await repo.ClearSignupStatusReasonsForUserAsync(userId, ct);

        var cancelled = await CancelActiveSignupsForUserAsync(userId, "Account deletion", ct);
        foreach (var (signupId, shiftId) in cancelled)
        {
            await auditLogService.LogAsync(
                ShiftsAuditActions.ShiftSignupCancelled, nameof(ShiftSignup), signupId,
                $"Cancelled signup (account deletion) for shift {shiftId}",
                jobName: nameof(ShiftSignupService));
        }

        await repo.DeleteVolunteerEventProfilesForUserAsync(userId, ct);
        await trackingRepo.DeleteVolunteerTrackingForUserAsync(userId, ct);
        await repo.DeleteVolunteerTagPreferencesForUserAsync(userId, ct);
    }

    public Task<IReadOnlyList<CalendarFeedItem>> GetPublicItemsForWindowAsync(Instant from, Instant to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<CalendarFeedItem>>([]);

    public async Task<IReadOnlyList<(Guid SignupId, Guid ShiftId)>> CancelActiveSignupsForUserAsync(
        Guid userId, string reason, CancellationToken ct = default)
    {
        return await repo.CancelActiveSignupsForUserAsync(userId, reason, ct);
    }

    public async Task<ToggleDaySignupOutcome> ToggleDayAsync(
        Guid userId,
        Guid shiftId,
        Guid eventSettingsId,
        bool privileged,
        CancellationToken ct = default)
    {
        var signups = await repo.GetForUsersAsync([userId], eventSettingsId, ct);
        var existing = signups.FirstOrDefault(s =>
            s.ShiftId == shiftId && s.Status is SignupStatus.Confirmed or SignupStatus.Pending);

        SignupResult result;
        bool signedUp;
        if (existing is not null)
        {
            result = await BailAsync(existing.Id, userId, "self-service toggle");
            signedUp = false;
        }
        else
        {
            result = await SignUpAsync(
                userId,
                shiftId,
                flags: privileged ? ShiftSignupRequestFlags.Privileged : ShiftSignupRequestFlags.None);
            signedUp = result.Success;
        }

        var canViewRestricted = privileged || await shiftMgmt.CanManageOrApproveAnyShiftsAsync(userId, ct);
        var after = await repo.GetForUsersAsync([userId], eventSettingsId, ct);

        return new ToggleDaySignupOutcome(result, signedUp, canViewRestricted, after);
    }

    public async Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken ct)
    {
        var movedCount = (await repo.ReassignToUserAsync(sourceUserId, targetUserId, updatedAt, ct)).Count;

        if (movedCount > 0)
        {
            await auditLogService.LogAsync(
                ShiftsAuditActions.ShiftSignupReassigned, AuditSubjectTypes.User, targetUserId,
                $"Reassigned {movedCount} shift signup(s) from merged source user {sourceUserId}",
                actorUserId,
                targetUserId, AuditSubjectTypes.User);
        }
    }

    private static readonly ResourceManager NoticeResources = new(typeof(ShiftsResource));

    // Lazy, to preserve the TeamService / ShiftSignupService constructor-cycle boundary.
    private ITeamService TeamService => serviceProvider.GetRequiredService<ITeamService>();

    internal async Task SendAssignmentNoticeAsync(Guid userId, Guid signupId, string resourceKey, params object[] args)
    {
        try
        {
            var (recipientId, culture) = await ResolveRecipientAsync(userId);
            var title = string.Format(culture, NoticeResources.GetString(resourceKey, culture)!, args);
            await notificationService.SendAsync(
                ShiftsNotificationSources.ShiftAssigned,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                title,
                [recipientId],
                body: null,
                actionUrl: "/Shifts",
                actionLabel: NoticeResources.GetString("Shifts_BrowseAvailable", culture));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch ShiftAssigned notification for user {UserId} signup {SignupId}", userId, signupId);
        }
    }

    private async Task<(Guid UserId, CultureInfo Culture)> ResolveRecipientAsync(Guid userId)
    {
        try
        {
            var person = await users.GetUserInfoAsync(userId);
            var language = person?.PreferredLanguage;
            return (person?.Id ?? userId, CultureInfo.GetCultureInfo(language.IsSupportedCultureCode() ? language! : "en"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve shift notice language for user {UserId}; using English", userId);
            return (userId, CultureInfo.GetCultureInfo("en"));
        }
    }

    internal async Task NotifyCoverageGapAsync(ShiftSignup signup, Shift shift)
    {
        try
        {
            if (shift.MinVolunteers <= 0) return;
            var confirmedCount = shift.ShiftSignups.Count(s => s.Status == SignupStatus.Confirmed);
            if (confirmedCount >= shift.MinVolunteers) return;

            await SendCoordinatorNoticeAsync(shift.Rota.TeamId, ShiftsNotificationSources.ShiftCoverageGap,
                NotificationClass.Actionable, NotificationPriority.High, culture => (
                    string.Format(culture, NoticeResources.GetString("Shifts_Notification_CoverageGapTitle", culture)!, shift.Rota.Name, shift.DayOffset),
                    string.Format(culture, NoticeResources.GetString("Shifts_Notification_CoverageGapBody", culture)!, confirmedCount, shift.MinVolunteers),
                    NoticeResources.GetString("Shifts_Notification_FindCover", culture)!));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch ShiftCoverageGap notification for signup {SignupId}", signup.Id);
        }
    }

    private Task DispatchSignupChangeNotificationAsync(
        ShiftSignup signup, Shift shift, string resourceKey, params object[] args) =>
        DispatchSignupChangeNotificationAsync(signup, shift, shift.Rota, resourceKey, args);

    private async Task DispatchSignupChangeNotificationAsync(
        ShiftSignup signup, Shift shift, Rota rota, string resourceKey, params object[] args)
    {
        try
        {
            // From Settings: the row's own calendar columns are dead.
            var calendar = await calendarResolver.GetAsync(rota.EventSettingsId);
            if (calendar is null) return;

            await SendCoordinatorNoticeAsync(rota.TeamId, ShiftsNotificationSources.ShiftSignupChange,
                NotificationClass.Informational, NotificationPriority.Normal, culture =>
                {
                    using var recipientCulture = new CultureScope(culture.Name);
                    var day = calendar.GateOpeningDate.PlusDays(shift.DayOffset).ToWeekdayDayMonth();
                    var description = string.Format(culture, NoticeResources.GetString(resourceKey, culture)!, args);
                    return (
                        string.Format(culture, NoticeResources.GetString("Shifts_Notification_SignupChangeTitle", culture)!, rota.Name),
                        $"{description} ({rota.Name}, {day})",
                        NoticeResources.GetString("Shifts_Notification_View", culture)!);
                });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch ShiftSignupChange notification for signup {SignupId}", signup.Id);
        }
    }

    private async Task SendCoordinatorNoticeAsync(
        Guid teamId, NotificationSourceDefinition source, NotificationClass notificationClass,
        NotificationPriority priority, Func<CultureInfo, (string Title, string Body, string ActionLabel)> createCopy)
    {
        var team = await TeamService.GetTeamAsync(teamId);
        var coordinatorIds = team?.Members
            .Where(m => m.Role == TeamMemberRole.Coordinator)
            .Select(m => m.UserId)
            .Distinct()
            .ToList() ?? [];
        var recipientsByCulture = new Dictionary<CultureInfo, List<Guid>>();
        var recipientIds = new HashSet<Guid>();
        foreach (var coordinatorId in coordinatorIds)
        {
            var (recipientId, culture) = await ResolveRecipientAsync(coordinatorId);
            if (!recipientIds.Add(recipientId)) continue;
            if (!recipientsByCulture.TryGetValue(culture, out var recipients))
                recipientsByCulture[culture] = recipients = [];
            recipients.Add(recipientId);
        }

        foreach (var (culture, recipients) in recipientsByCulture)
        {
            try
            {
                var (title, body, actionLabel) = createCopy(culture);
                await notificationService.SendAsync(source, notificationClass, priority, title, recipients,
                    body: body, actionUrl: $"/Shifts?departmentId={teamId}", actionLabel: actionLabel);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to dispatch {Source} notification for team {TeamId} in {Culture}",
                    source, teamId, culture.Name);
            }
        }
    }

    public async Task<IReadOnlyList<ShiftSignupSummary>> GetByUserAsync(Guid userId, Guid? eventSettingsId = null, CancellationToken ct = default)
    {
        var signups = await repo.GetForUsersAsync([userId], eventSettingsId, ct);
        var result = new List<ShiftSignupSummary>(signups.Count);
        foreach (var signup in signups)
        {
            if (await calendarResolver.GetAsync(signup.Shift.Rota.EventSettingsId, ct) is { } calendar)
                result.Add(ShiftUserSummaryProjection.ToSummary(signup, calendar));
        }

        return result;
    }

    public async Task<IReadOnlyList<NoShowHistoryEntry>> GetNoShowHistoryAsync(Guid userId, CancellationToken ct = default)
    {
        var signups = (await repo.GetForUsersAsync([userId], ct: ct))
            .Where(s => s.Status == SignupStatus.NoShow)
            .ToList();

        var calendarsById = new Dictionary<Guid, BurnSettingsInfo>();
        foreach (var id in signups.Select(s => s.Shift.Rota.EventSettingsId).Distinct())
        {
            var calendar = await calendarResolver.GetAsync(id, ct);
            if (calendar is not null) calendarsById[id] = calendar;
        }

        return signups
            .Where(s => calendarsById.ContainsKey(s.Shift.Rota.EventSettingsId))
            .Select(s =>
            {
                var rota = s.Shift.Rota;
                var calendar = calendarsById[rota.EventSettingsId];
                return new NoShowHistoryEntry(
                    ShiftLabel: rota.Name,
                    TeamId: rota.TeamId,
                    ShiftStart: s.Shift.GetAbsoluteStart(calendar),
                    TimeZoneId: calendar.TimeZoneId,
                    ReviewedByUserId: s.ReviewedByUserId,
                    ReviewedAt: s.ReviewedAt);
            }).ToList();
    }

    public async Task<IReadOnlyList<CalendarFeedItem>> GetCalendarItemsForUserAsync(Guid userId, CancellationToken ct)
    {
        // Active commitments only; history is out of feed scope (spec 2026-06-09-ical-feed).
        var signups = (await repo.GetForUsersAsync([userId], ct: ct))
            .Where(ss => ss.Status is SignupStatus.Confirmed or SignupStatus.Pending)
            .ToList();
        if (signups.Count == 0) return [];

        var teamsById = await TeamService.GetTeamsAsync(ct);

        var calendarsById = new Dictionary<Guid, BurnSettingsInfo>();
        foreach (var id in signups.Select(ss => ss.Shift.Rota.EventSettingsId).Distinct())
        {
            var calendar = await calendarResolver.GetAsync(id, ct);
            if (calendar is not null) calendarsById[id] = calendar;
        }

        return signups
            .Where(ss => calendarsById.ContainsKey(ss.Shift.Rota.EventSettingsId))
            .Select(ss =>
            {
                var es = calendarsById[ss.Shift.Rota.EventSettingsId];
                var summary = teamsById.TryGetValue(ss.Shift.Rota.TeamId, out var team)
                    ? $"{team.Name}: {ss.Shift.Rota.Name}"
                    : ss.Shift.Rota.Name;
                if (ss.Status == SignupStatus.Pending)
                    summary += $" {localizer["Shifts_Pending"].Value}";

                var description = string.Join("\n\n", new[] { ss.Shift.Description, ss.Shift.Rota.PracticalInfo }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));

                return new CalendarFeedItem(
                    Uid: $"shift-{ss.Id}@humans.nobodies.team",
                    Source: "Shifts",
                    Summary: summary,
                    Description: description.Length == 0 ? null : description,
                    Start: ss.Shift.GetAbsoluteStart(es),
                    End: ss.Shift.GetAbsoluteEnd(es),
                    Location: null,
                    Url: "/Shifts/Mine");
            }).ToList();
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var signups = (await repo.GetForUsersAsync([userId], ct: ct))
            .OrderByDescending(ss => ss.CreatedAt)
            .ToList();

        // Includes deactivated teams, so historical signups still resolve a name.
        var referencedTeamIds = signups
            .Select(ss => ss.Shift.Rota.TeamId)
            .Distinct()
            .ToList();
        var teamsByIdLookup = await TeamService.GetTeamsAsync(ct);
        var teamNamesById = referencedTeamIds
            .Where(teamsByIdLookup.ContainsKey)
            .ToDictionary(id => id, id => teamsByIdLookup[id].Name);

        var volunteerEventProfile = await repo.GetVolunteerEventProfileAsync(userId, ct);
        IReadOnlyList<VolunteerEventProfile> volunteerEventProfiles =
            volunteerEventProfile is null ? [] : [volunteerEventProfile];
        var generalAvailability = await trackingRepo.GetAvailabilityForUserAsync(userId, ct: ct);
        var tagPreferences = await repo.GetVolunteerTagPreferencesForUsersAsync([userId], ct);
        var exportUserIds = (await users.GetUserInfoAsync(userId, ct))?.AllUserIds ?? [userId];
        var buildStatuses = await trackingRepo.GetBuildStatusesForEventAsync(null, exportUserIds, ct);

        var distinctEventSettingsIds = signups.Select(ss => ss.Shift.Rota.EventSettingsId)
            .Concat(generalAvailability.Select(ga => ga.EventSettingsId))
            .Concat(buildStatuses.Select(bs => bs.EventSettingsId))
            .Distinct();
        var eventNamesById = new Dictionary<Guid, string>();
        foreach (var id in distinctEventSettingsIds)
        {
            var calendar = await calendarResolver.GetAsync(id, ct);
            if (calendar is not null)
                eventNamesById[id] = calendar.EventName;
        }

        var signupSlice = new UserDataSlice(ShiftSignups, signups.Select(ss => new
        {
            EventName = eventNamesById.TryGetValue(ss.Shift.Rota.EventSettingsId, out var signupEventName) ? signupEventName : string.Empty,
            Department = teamNamesById.TryGetValue(ss.Shift.Rota.TeamId, out var teamName) ? teamName : null,
            RotaName = ss.Shift.Rota.Name,
            ss.Shift.DayOffset,
            ss.Shift.IsAllDay,
            ss.Status,
            ss.Enrolled,
            ss.StatusReason,
            CreatedAt = ss.CreatedAt.ToIso8601(),
            UpdatedAt = ss.UpdatedAt.ToIso8601(),
            ReviewedAt = ss.ReviewedAt.ToIso8601()
        }).ToList());

        var vepSlice = new UserDataSlice(VolunteerEventProfiles, volunteerEventProfiles.Select(vep => new
        {
            vep.Skills,
            vep.Quirks,
            vep.Languages,
            CreatedAt = vep.CreatedAt.ToIso8601(),
            UpdatedAt = vep.UpdatedAt.ToIso8601()
        }).ToList());

        var availabilitySlice = new UserDataSlice(GeneralAvailability, generalAvailability.Select(ga => new
        {
            EventName = eventNamesById.TryGetValue(ga.EventSettingsId, out var eventName) ? eventName : string.Empty,
            ga.AvailableDayOffsets,
            UpdatedAt = ga.UpdatedAt.ToIso8601()
        }).ToList());

        var tagPreferenceSlice = new UserDataSlice(ShiftTagPreferences, tagPreferences.Select(vtp => new
        {
            TagName = vtp.ShiftTag.Name
        }).ToList());

        var buildStatusSlice = new UserDataSlice(VolunteerBuildStatuses, buildStatuses.Select(bs => new
        {
            bs.EventSettingsId,
            EventName = eventNamesById.GetValueOrDefault(bs.EventSettingsId, string.Empty),
            BarrioSetupStartDate = bs.BarrioSetupStartDate.ToInvariantDate(),
            bs.Notes,
            SetAt = bs.SetAt.ToIso8601(),
            DayOffs = bs.DayOffs.Select(day => new
            {
                day.DayOffset,
                day.Reason,
                MarkedAt = day.MarkedAt.ToIso8601()
            }).ToList()
        }).ToList());

        return [signupSlice, vepSlice, availabilitySlice, tagPreferenceSlice, buildStatusSlice];
    }

    private async Task<IReadOnlyList<ShiftSignup>> GetActiveUserSignupsAsync(Guid userId)
    {
        var signups = await repo.GetForUsersAsync([userId]);
        return signups
            .Where(signup => signup.Status is SignupStatus.Pending or SignupStatus.Confirmed)
            .ToList();
    }

    private async Task<string?> CheckOverlapAsync(Guid userId, Shift targetShift, BurnSettingsInfo es)
    {
        var targetStart = targetShift.GetAbsoluteStart(es);
        var targetEnd = targetShift.GetAbsoluteEnd(es);

        var userSignups = await GetActiveUserSignupsAsync(userId);

        IReadOnlyDictionary<Guid, string>? teamNames = null;

        foreach (var existing in userSignups)
        {
            if (existing.ShiftId == targetShift.Id) continue;
            if (existing.Status != SignupStatus.Confirmed) continue;

            var existingEs = await calendarResolver.GetAsync(existing.Shift.Rota.EventSettingsId);
            if (existingEs is null) continue;
            var existingStart = existing.Shift.GetAbsoluteStart(existingEs);
            var existingEnd = existing.Shift.GetAbsoluteEnd(existingEs);

            if (targetStart < existingEnd && targetEnd > existingStart)
            {
                var tz = existingEs.TimeZone;
                var dateStr = existingStart.InZone(tz).ToDateTimeUnspecified().ToMonthDayTime();

                if (teamNames is null)
                {
                    var teamsById = await TeamService.GetTeamsAsync();
                    teamNames = userSignups
                        .Select(s => s.Shift.Rota.TeamId)
                        .Distinct()
                        .Where(teamsById.ContainsKey)
                        .ToDictionary(id => id, id => teamsById[id].Name);
                }
                var teamName = teamNames.GetValueOrDefault(existing.Shift.Rota.TeamId, localizer["Shifts_Signup_UnknownTeam"]);

                return localizer["Shifts_Signup_TimeConflict", existing.Shift.Rota.Name, teamName, dateStr];
            }
        }

        return null;
    }

    private async Task<string?> CheckEeCapAsync(BurnSettingsInfo es, int dayOffset)
    {
        var availableSlots = EarlyEntryCapacityCalculator.GetAvailableEeSlots(es, dayOffset);
        if (availableSlots <= 0)
            return localizer["Shifts_Signup_RangeEarlyEntryCapacityDays", FormatRangeDayList(es, [dayOffset])];

        var currentEeCount = (await repo
            .GetUserIdsForDayAsync(es.Id, dayOffset, ShiftDayUserStatusScope.ConfirmedOnly))
            .Count;

        if (currentEeCount >= availableSlots)
            return localizer["Shifts_Signup_RangeEarlyEntryCapacityDays", FormatRangeDayList(es, [dayOffset])];

        return null;
    }

    private RangeSignupCandidateSelection PruneDuplicateRangeShifts(
        List<Shift> shiftsInRange,
        IReadOnlyCollection<ShiftSignup> existingSignups,
        IEventSettingsInfo eventSettings,
        bool skipConflicts)
    {
        var shiftIdsInRange = shiftsInRange.Select(s => s.Id).ToHashSet();
        var activeShiftIds = existingSignups
            .Where(s => shiftIdsInRange.Contains(s.ShiftId))
            .Select(s => s.ShiftId)
            .ToHashSet();

        if (activeShiftIds.Count == 0)
            return new RangeSignupCandidateSelection(shiftsInRange, [], null);

        if (!skipConflicts)
            return new RangeSignupCandidateSelection(
                shiftsInRange,
                [],
                SignupResult.Fail(localizer["Shifts_Signup_RangeDuplicate"]));

        var alreadySignedUpDays = shiftsInRange
            .Where(s => activeShiftIds.Contains(s.Id))
            .Select(s => s.DayOffset)
            .ToList();
        var dayList = FormatRangeDayList(eventSettings, alreadySignedUpDays);
        var warnings = new List<string> { localizer["Shifts_Signup_RangeDuplicateWarning", dayList].Value };
        var remainingShifts = shiftsInRange
            .Where(s => !activeShiftIds.Contains(s.Id))
            .ToList();

        return new RangeSignupCandidateSelection(remainingShifts, warnings, null);
    }

    private async Task<RangeSignupCandidateSelection> PruneConflictingRangeShiftsAsync(
        List<Shift> shiftsInRange,
        IReadOnlyCollection<ShiftSignup> existingSignups,
        IEventSettingsInfo eventSettings,
        IReadOnlyList<string> existingWarnings,
        bool skipConflicts)
    {
        var conflictingDays = await GetConflictingRangeDaysAsync(shiftsInRange, existingSignups, eventSettings);
        if (conflictingDays.Count == 0)
            return new RangeSignupCandidateSelection(shiftsInRange, existingWarnings.ToList(), null);

        var dayList = FormatRangeDayList(eventSettings, conflictingDays);
        if (!skipConflicts)
            return new RangeSignupCandidateSelection(
                shiftsInRange,
                existingWarnings.ToList(),
                SignupResult.Fail(localizer["Shifts_Signup_RangeConflictDays", dayList].Value));

        var warnings = existingWarnings.Append(localizer["Shifts_Signup_RangeConflictDays", dayList].Value).ToList();
        var remainingShifts = shiftsInRange
            .Where(s => !conflictingDays.Contains(s.DayOffset))
            .ToList();

        return new RangeSignupCandidateSelection(remainingShifts, warnings, null);
    }

    private async Task<List<int>> GetConflictingRangeDaysAsync(
        IReadOnlyList<Shift> shiftsInRange,
        IEnumerable<ShiftSignup> existingSignups,
        IEventSettingsInfo eventSettings)
    {
        // Range candidates are all-day shifts from one rota, so two on one date overlap even with no bookings.
        var conflictingDays = shiftsInRange.GroupBy(s => s.DayOffset)
            .Where(day => day.Count() > 1)
            .Select(day => day.Key)
            .ToHashSet();
        var confirmedSignups = existingSignups.Where(s => s.Status == SignupStatus.Confirmed).ToList();
        foreach (var shift in shiftsInRange)
        {
            if (conflictingDays.Contains(shift.DayOffset)) continue;
            var shiftStart = shift.GetAbsoluteStart(eventSettings);
            var shiftEnd = shift.GetAbsoluteEnd(eventSettings);

            foreach (var existing in confirmedSignups)
            {
                var existingCalendar = await calendarResolver.GetAsync(existing.Shift.Rota.EventSettingsId);
                if (existingCalendar is null) continue;
                var existingStart = existing.Shift.GetAbsoluteStart(existingCalendar);
                var existingEnd = existing.Shift.GetAbsoluteEnd(existingCalendar);

                if (shiftStart < existingEnd && shiftEnd > existingStart)
                {
                    conflictingDays.Add(shift.DayOffset);
                    break;
                }
            }
        }

        return shiftsInRange.Select(s => s.DayOffset).Where(conflictingDays.Contains).Distinct().ToList();
    }

    private async Task<RangeCapacitySelection> SelectCapacityAvailableRangeShiftsAsync(List<Shift> shiftsInRange)
    {
        var shiftIdsInRange = shiftsInRange.Select(s => s.Id).ToHashSet();
        var signupCounts = await repo.GetConfirmedSignupCountsByShiftAsync(shiftIdsInRange);
        var fullDays = shiftsInRange
            .Where(s => signupCounts.GetValueOrDefault(s.Id) >= s.MaxVolunteers)
            .Select(s => s.DayOffset)
            .ToList();

        var availableShifts = shiftsInRange
            .Where(s => signupCounts.GetValueOrDefault(s.Id) < s.MaxVolunteers)
            .ToList();

        return new RangeCapacitySelection(availableShifts, fullDays);
    }

    private static string FormatRangeDayList(IEventSettingsInfo eventSettings, IEnumerable<int> dayOffsets)
        => string.Join(", ", dayOffsets.Select(offset => eventSettings.GateOpeningDate.PlusDays(offset).ToWeekdayDayMonth()));

    private static List<Shift> SelectAllDayRangeShifts(Rota rota, int startDayOffset, int endDayOffset) =>
        rota.Shifts
            .Where(s => s.IsAllDay && s.DayOffset >= startDayOffset && s.DayOffset <= endDayOffset)
            .OrderBy(s => s.DayOffset)
            .ToList();

    private sealed record RangeSignupCandidateSelection(
        List<Shift> Shifts,
        List<string> Warnings,
        SignupResult? Failure);

    private sealed record RangeCapacitySelection(
        List<Shift> AvailableShifts,
        List<int> FullDayOffsets);
}
