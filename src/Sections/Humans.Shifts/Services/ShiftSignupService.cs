using Humans.Auth.Contracts;
using Humans.Base.Extensions;
using Humans.Base.Threading;
using Humans.AuditLog.Contracts;
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
    IAdminAuthorizationService adminAuthorization,
    IServiceProvider serviceProvider,
    IClock clock,
    ILogger<ShiftSignupService> logger,
    IUserService users,
    IStringLocalizer<ShiftsResource> localizer) : IShiftSignupService, IUserMerge, ICalendarFeedContributor
{
    private readonly ShiftSignupNotices notices = new(notificationService, calendarResolver, users, serviceProvider, logger);
    private readonly ShiftSignupPersonalData personalData = new(repo, trackingRepo, calendarResolver, users, serviceProvider, localizer);
    private readonly ShiftSignupSelection signupSelection = new(repo, calendarResolver, localizer, serviceProvider);
    // Released after the enrollment save, before audit and notices; refusals and failures release via using.
    private static readonly TrackedLock EnrollmentLock = new("Shifts.Enrollment");

    /// <summary>GDPR export key.</summary>
    internal const string ShiftSignups = ShiftSignupPersonalData.ShiftSignups;
    internal const string VolunteerEventProfiles = ShiftSignupPersonalData.VolunteerEventProfiles;
    internal const string GeneralAvailability = ShiftSignupPersonalData.GeneralAvailability;
    internal const string VolunteerBuildStatuses = ShiftSignupPersonalData.VolunteerBuildStatuses;
    internal const string ShiftTagPreferences = ShiftSignupPersonalData.ShiftTagPreferences;

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
        using var enrollmentLock = await EnrollmentLock.AcquireAsync(logger);
        if (await ResolveEnrolleeAsync(userId, enrollment) is not { } enrolleeId)
            return SignupResult.Fail(localizer["Shifts_AccountCannotSignUp"]);
        userId = enrolleeId;

        var activeShiftIds = await repo.GetActiveShiftIdsForUserAsync(userId, [shiftId]);
        if (activeShiftIds.Contains(shiftId))
            return SignupResult.Fail(localizer["Shifts_Signup_AlreadySignedUp"]);

        var shift = await repo.GetShiftAsync(shiftId, ShiftReadShape.Context);
        if (shift is null) return SignupResult.Fail(localizer["Shifts_Signup_ShiftNotFound"]);

        var calendar = await calendarResolver.GetAsync(shift.Rota.EventSettingsId);
        if (calendar is null) return SignupResult.Fail(localizer["Shifts_EventCalendarNotConfigured"]);
        var now = clock.GetCurrentInstant();
        var (isPrivileged, canApprove) = await ResolvePrivilegeAsync(userId, shift.Rota.TeamId, enrollment);

        if (!shift.Rota.EventSettings.IsShiftBrowsingOpen && !isPrivileged)
            return SignupResult.Fail(localizer["Shifts_Signup_BrowsingClosed"]);

        if ((!shift.Rota.IsVisibleToVolunteers || shift.AdminOnly) && !isPrivileged)
            return SignupResult.Fail(localizer["Shifts_Signup_Restricted"]);

        if (shift.IsEarlyEntry && calendar.IsEarlyEntryClosed(now) && !isPrivileged)
            return SignupResult.Fail(localizer["Shifts_Signup_EarlyEntryClosed"]);

        var overlapWarning = await signupSelection.CheckOverlapAsync(userId, shift, calendar);
        if (overlapWarning is not null)
            return SignupResult.Fail(overlapWarning);

        var confirmedCount = shift.ShiftSignups.Count(d => d.Status == SignupStatus.Confirmed);
        if (confirmedCount >= shift.MaxVolunteers)
            return SignupResult.Fail(localizer["Shifts_Signup_AtCapacity"]);

        var warning = shift.IsEarlyEntry ? await signupSelection.CheckEeCapAsync(calendar, shift.DayOffset) : null;

        var autoConfirm = await IsAutoConfirmedAsync(userId, shift.Rota, enrollment, canApprove);
        var signup = StageSignup(userId, shift.Id, enrollment, now, autoConfirm, blockId: null);

        await repo.SaveChangesAsync();
        enrollmentLock.Dispose();
        await AuditEnrollmentAsync(userId, shift.Rota, calendar, [(signup, shift.DayOffset)], enrollment, autoConfirm, range: false);

        if (enrollment.Voluntold)
        {
            await notices.DispatchSignupChangeNotificationAsync(signup, shift,
                "Shifts_Notification_ChangeAssigned", shift.Rota.Name, shift.DayOffset);
            if (shift.GetAbsoluteEnd(calendar) > now)
                await notices.SendAssignmentNoticeAsync(userId, signup.Id, "Shifts_Notification_AssignedDay", shift.Rota.Name, shift.DayOffset);
        }
        else if (autoConfirm)
        {
            await notices.DispatchSignupChangeNotificationAsync(signup, shift,
                "Shifts_Notification_ChangeConfirmed", shift.Rota.Name, shift.DayOffset);
        }

        return SignupResult.Ok(signup.Id, warning);
    }

    private async Task<SignupResult> EnrollRangeAsync(
        Guid userId, Guid rotaId, int startDayOffset, int endDayOffset, Enrollment enrollment)
    {
        using var enrollmentLock = await EnrollmentLock.AcquireAsync(logger);
        if (await ResolveEnrolleeAsync(userId, enrollment) is not { } enrolleeId)
            return SignupResult.Fail(localizer["Shifts_AccountCannotSignUp"]);
        userId = enrolleeId;

        var skipConflicts = enrollment.Flags.HasFlag(ShiftSignupRequestFlags.SkipConflicts);
        var rota = await repo.GetRotaAsync(rotaId, RotaReadShape.EventSettings | RotaReadShape.Shifts);
        if (rota is null) return SignupResult.Fail(localizer["Shifts_Signup_RotaNotFound"]);

        var calendar = await calendarResolver.GetAsync(rota.EventSettingsId);
        if (calendar is null) return SignupResult.Fail(localizer["Shifts_EventCalendarNotConfigured"]);
        var now = clock.GetCurrentInstant();
        var (isPrivileged, canApprove) = await ResolvePrivilegeAsync(userId, rota.TeamId, enrollment);

        if (!rota.EventSettings.IsShiftBrowsingOpen && !isPrivileged)
            return SignupResult.Fail(localizer["Shifts_Signup_BrowsingClosed"]);

        if (!rota.IsVisibleToVolunteers && !isPrivileged)
            return SignupResult.Fail(localizer["Shifts_Signup_RangeRestricted"]);

        if (rota.Period == RotaPeriod.Build && calendar.IsEarlyEntryClosed(now) && !isPrivileged)
            return SignupResult.Fail(localizer["Shifts_Signup_EarlyEntryClosed"]);

        var shiftsInRange = ShiftSignupSelection.SelectAllDayRangeShifts(rota, startDayOffset, endDayOffset);

        if (!isPrivileged && shiftsInRange.Any(s => s.AdminOnly))
            return SignupResult.Fail(localizer["Shifts_Signup_RangeRestricted"]);

        var existingSignups = await signupSelection.GetActiveUserSignupsAsync(userId);

        var duplicateSelection = signupSelection.PruneDuplicateRangeShifts(shiftsInRange, existingSignups, calendar, skipConflicts);
        if (duplicateSelection.Failure is not null)
            return duplicateSelection.Failure;

        var conflictSelection = await signupSelection.PruneConflictingRangeShiftsAsync(
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

        var capacitySelection = await signupSelection.SelectCapacityAvailableRangeShiftsAsync(shiftsInRange);
        if (capacitySelection.AvailableShifts.Count == 0)
            return SignupResult.Fail(localizer["Shifts_Signup_RangeAtCapacity"]);

        string? warning = conflictSelection.Warnings.Count > 0
            ? string.Join(" ", conflictSelection.Warnings)
            : null;
        if (capacitySelection.FullDayOffsets.Count > 0)
        {
            var dayList = ShiftSignupSelection.FormatRangeDayList(calendar, capacitySelection.FullDayOffsets);
            warning = AppendRangeWarning(warning, localizer["Shifts_Signup_RangeCapacityDays", dayList].Value);
        }

        var availableShifts = capacitySelection.AvailableShifts;

        if (rota.Period == RotaPeriod.Build)
            warning = await AppendEarlyEntryRangeWarningAsync(warning, availableShifts, calendar);

        var blockId = Guid.NewGuid();
        var autoConfirm = await IsAutoConfirmedAsync(userId, rota, enrollment, canApprove);
        var created = availableShifts
            .Select(shift => (Signup: StageSignup(userId, shift.Id, enrollment, now, autoConfirm, blockId), shift.DayOffset))
            .ToList();

        await repo.SaveChangesAsync();
        enrollmentLock.Dispose();
        await AuditEnrollmentAsync(userId, rota, calendar, created, enrollment, autoConfirm, range: true);

        var lastSignup = created[^1].Signup;
        if (enrollment.Voluntold)
        {
            await notices.DispatchSignupChangeNotificationAsync(lastSignup, availableShifts[^1], rota,
                "Shifts_Notification_ChangeAssignedRange", rota.Name, availableShifts.Count);
            if (availableShifts.Any(s => s.GetAbsoluteEnd(calendar) > now))
            {
                await notices.SendAssignmentNoticeAsync(userId, lastSignup.Id, availableShifts.Count == 1
                    ? "Shifts_Notification_AssignedRangeSingular" : "Shifts_Notification_AssignedRange", rota.Name, availableShifts.Count);
            }
        }
        else if (autoConfirm)
        {
            await notices.DispatchSignupChangeNotificationAsync(lastSignup, availableShifts[^1], rota,
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
        var existingSignups = await signupSelection.GetActiveUserSignupsAsync(group[0].UserId);
        var conflictingDays = await signupSelection.GetConflictingRangeDaysAsync(
            group.Select(s => s.Shift).ToList(), existingSignups, calendar);
        if (conflictingDays.Count > 0)
            warnings.Add(localizer["Shifts_Signup_RangeConflictDays", ShiftSignupSelection.FormatRangeDayList(calendar, conflictingDays)].Value);

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
                if (await signupSelection.CheckEeCapAsync(calendar, signup.Shift.DayOffset) is { } eeWarning)
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
            await notices.CheckAndNotifyCoverageGapAsync(signup, signup.Shift);
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
            ? notices.DispatchSignupChangeNotificationAsync(first, first.Shift, resourceKey, first.Shift.Rota.Name, first.Shift.DayOffset)
            : notices.DispatchSignupChangeNotificationAsync(first, first.Shift, resourceKey + "Range", first.Shift.Rota.Name, changed.Count);
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

        await notices.DispatchSignupChangeNotificationAsync(signup, signup.Shift,
            "Shifts_Notification_ChangeRemoved", signup.Shift.Rota.Name, signup.Shift.DayOffset);
        await notices.CheckAndNotifyCoverageGapAsync(signup, signup.Shift);

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
            var eeWarning = await signupSelection.CheckEeCapAsync(eventSettings, dayOffset);
            if (eeWarning is not null)
                fullEeDays.Add(dayOffset);
        }

        if (fullEeDays.Count == 0)
            return warning;

        var eeDayList = ShiftSignupSelection.FormatRangeDayList(eventSettings, fullEeDays);
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

    public Task<IReadOnlyList<ShiftSignupSummary>> GetByUserAsync(
        Guid userId, Guid? eventSettingsId = null, CancellationToken ct = default) =>
        personalData.GetByUserAsync(userId, eventSettingsId, ct);

    public async Task<ShiftSignupTeamProbe?> GetTeamProbeAsync(Guid id, ShiftSignupTeamProbeScope scope, CancellationToken ct = default) =>
        (await serviceProvider.GetRequiredService<IShiftRotaRows>().GetRotaRowsAsync(ct: ct))
            .SelectMany(r => r.Shifts.SelectMany(s => s.Signups
                .Where(signup => scope == ShiftSignupTeamProbeScope.Signup ? signup.Id == id : signup.SignupBlockId == id)
                .Select(signup => new ShiftSignupTeamProbe(signup.Id, s.Shift.Id, r.Rota.TeamId))))
            .FirstOrDefault();

    public Task<IReadOnlyList<NoShowHistoryEntry>> GetNoShowHistoryAsync(Guid userId, CancellationToken ct = default) =>
        personalData.GetNoShowHistoryAsync(userId, ct);

    private bool IsStatusReasonTooLong(string? reason, Guid signupId, Guid actorUserId)
    {
        if (reason is not { Length: > 1000 }) return false;
        logger.LogWarning("Rejected status reason longer than 1000 characters for signup {SignupId} by actor {ActorId}",
            signupId, actorUserId);
        return true;
    }

    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        personalData.ContributeForUserAsync(userId, ct);

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

    public Task<IReadOnlyList<CalendarFeedItem>> GetCalendarItemsForUserAsync(Guid userId, CancellationToken ct) =>
        personalData.GetCalendarItemsForUserAsync(userId, ct);

    public async Task<IReadOnlyList<(Guid SignupId, Guid ShiftId)>> CancelActiveSignupsForUserAsync(
        Guid userId, string reason, CancellationToken ct = default)
    {
        return await repo.CancelActiveSignupsForUserAsync(userId, reason, ct);
    }

    public async Task<int> DeleteAllForUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default)
    {
        await adminAuthorization.RequireCurrentUserIsAdminAsync(ct);
        return await repo.DeleteAllForUsersAsync(userIds, ct);
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

        var canViewRestricted = privileged || (await shiftMgmt.GetCoordinatorTeamIdsAsync(userId)).Count > 0;
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

}
