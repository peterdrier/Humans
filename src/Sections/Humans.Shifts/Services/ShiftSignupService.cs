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
    CachingShiftViewService viewInvalidator,
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

    public async Task<SignupResult> SignUpAsync(
        Guid userId,
        Guid shiftId,
        Guid? actorUserId = null,
        ShiftSignupRequestFlags flags = ShiftSignupRequestFlags.None)
    {
        using var enrollment = await EnrollmentLock.AcquireAsync(logger);
        var user = await users.GetUserInfoAsync(userId);
        if (user?.State != UserState.Active)
            return SignupResult.Fail(localizer["Shifts_AccountCannotSignUp"]);
        userId = user.Id;

        var isPrivileged = flags.HasFlag(ShiftSignupRequestFlags.Privileged);
        var activeShiftIds = await repo.GetActiveShiftIdsForUserAsync(userId, [shiftId]);
        if (activeShiftIds.Contains(shiftId))
            return SignupResult.Fail(localizer["Shifts_Signup_AlreadySignedUp"]);

        var shift = await repo.GetShiftAsync(shiftId, ShiftReadShape.Context);
        if (shift is null) return SignupResult.Fail(localizer["Shifts_Signup_ShiftNotFound"]);

        var localEs = shift.Rota.EventSettings;
        var calendar = await calendarResolver.GetAsync(shift.Rota.EventSettingsId);
        if (calendar is null) return SignupResult.Fail(localizer["Shifts_EventCalendarNotConfigured"]);
        var now = clock.GetCurrentInstant();
        bool? canApprove = isPrivileged ? null : await IsPrivilegedAsync(userId, shift.Rota.TeamId);
        isPrivileged = isPrivileged || canApprove == true;

        if (!localEs.IsShiftBrowsingOpen && !isPrivileged)
            return SignupResult.Fail(localizer["Shifts_Signup_BrowsingClosed"]);

        if ((!shift.Rota.IsVisibleToVolunteers || shift.AdminOnly) && !isPrivileged)
            return SignupResult.Fail(localizer["Shifts_Signup_Restricted"]);

        if (shift.IsEarlyEntry && calendar.IsEarlyEntryClosed(now) && !isPrivileged)
            return SignupResult.Fail(localizer["Shifts_Signup_EarlyEntryClosed"]);

        var overlapWarning = await signupSelection.CheckOverlapAsync(userId, shift, calendar);
        if (overlapWarning is not null)
            return SignupResult.Fail(overlapWarning);

        string? warning = null;
        var confirmedCount = shift.ShiftSignups.Count(d => d.Status == SignupStatus.Confirmed);
        if (confirmedCount >= shift.MaxVolunteers)
            return SignupResult.Fail(localizer["Shifts_Signup_AtCapacity"]);

        if (shift.IsEarlyEntry)
        {
            var eeWarning = await signupSelection.CheckEeCapAsync(calendar, shift.DayOffset);
            if (eeWarning is not null)
                warning = warning is null ? eeWarning : $"{warning} {eeWarning}";
        }

        // Public rotas auto-confirm whatever the admission or consent status; only RequireApproval parks as Pending.
        var autoConfirm = shift.Rota.Policy == SignupPolicy.Public
            || (canApprove ?? await IsPrivilegedAsync(userId, shift.Rota.TeamId));

        var signup = NewSignup(userId, shiftId, actorUserId, now, autoConfirm);

        repo.AddRange([signup]);

        try
        {
            await repo.SaveChangesAsync();
        }
        finally
        {
            InvalidateSignupCaches(localEs.Id, shiftId);
        }
        enrollment.Dispose();
        var shiftDate = FormatAuditDay(calendar, shift.DayOffset);
        var statusSuffix = autoConfirm ? "confirmed" : "pending";
        await auditLogService.LogAsync(
            ShiftsAuditActions.ShiftSignupCreated, nameof(ShiftSignup), signup.Id,
            $"shift '{shift.Rota.Name}' on {shiftDate} ({statusSuffix})",
            actorUserId ?? userId,
            userId, AuditSubjectTypes.User);

        if (autoConfirm)
        {
            await notices.DispatchSignupChangeNotificationAsync(signup, shift,
                "Shifts_Notification_ChangeConfirmed", shift.Rota.Name, shift.DayOffset);
        }

        return SignupResult.Ok(signup.Id, warning);
    }

    public async Task<SignupResult> ApproveAsync(Guid signupId, Guid reviewerUserId)
    {
        using var enrollment = await EnrollmentLock.AcquireAsync(logger);
        var signup = await repo.GetByIdForMutationAsync(signupId);
        if (signup is null) return SignupResult.Fail(localizer["Shifts_SignupNotFound"]);

        if (signup.Status != SignupStatus.Pending)
            return SignupResult.Fail($"Cannot approve signup in {signup.Status} state.");

        var calendar = await calendarResolver.GetAsync(signup.Shift.Rota.EventSettingsId);
        if (calendar is null) return SignupResult.Fail(localizer["Shifts_EventCalendarNotConfigured"]);

        var overlapWarning = await signupSelection.CheckOverlapAsync(signup.UserId, signup.Shift, calendar);
        string? warning = null;
        if (overlapWarning is not null)
            warning = $"Warning: {overlapWarning}";

        var confirmedCount = signup.Shift.ShiftSignups.Count(d => d.Status == SignupStatus.Confirmed);
        if (confirmedCount >= signup.Shift.MaxVolunteers)
            return SignupResult.Fail("Cannot approve: shift is at capacity.");

        if (signup.Shift.IsEarlyEntry)
        {
            var eeWarning = await signupSelection.CheckEeCapAsync(calendar, signup.Shift.DayOffset);
            if (eeWarning is not null)
                warning = warning is null ? $"Warning: {eeWarning}" : $"{warning} {eeWarning}";
        }

        var now = clock.GetCurrentInstant();
        if (signup.Shift.IsEarlyEntry && calendar.IsEarlyEntryClosed(now))
        {
            var isPrivileged = await IsPrivilegedAsync(reviewerUserId, signup.Shift.Rota.TeamId);
            if (!isPrivileged)
                return SignupResult.Fail("Cannot approve build shift signups after early entry close.");
        }

        signup.Confirm(reviewerUserId, clock);

        try
        {
            await repo.SaveChangesAsync();
        }
        finally
        {
            InvalidateSignupCaches(signup.Shift.Rota.EventSettingsId, signup.ShiftId);
        }
        enrollment.Dispose();
        await auditLogService.LogAsync(
            ShiftsAuditActions.ShiftSignupConfirmed, nameof(ShiftSignup), signup.Id,
            $"shift '{signup.Shift.Rota.Name}'",
            reviewerUserId,
            signup.UserId, AuditSubjectTypes.User);

        await notices.DispatchSignupChangeNotificationAsync(signup, signup.Shift,
            "Shifts_Notification_ChangeApproved", signup.Shift.Rota.Name, signup.Shift.DayOffset);

        return SignupResult.Ok(signup.Id, warning);
    }

    public async Task<SignupResult> RefuseAsync(Guid signupId, Guid reviewerUserId, string? reason)
    {
        var signup = await repo.GetByIdForMutationAsync(signupId);
        if (signup is null) return SignupResult.Fail(localizer["Shifts_SignupNotFound"]);
        if (IsStatusReasonTooLong(reason, signup.Id, reviewerUserId))
            return SignupResult.Fail(localizer["Shifts_StatusReasonTooLong"]);

        signup.Refuse(reviewerUserId, clock, reason);

        try
        {
            await repo.SaveChangesAsync();
        }
        finally
        {
            InvalidateSignupCaches(signup.Shift.Rota.EventSettingsId, signup.ShiftId);
        }

        await auditLogService.LogAsync(
            ShiftsAuditActions.ShiftSignupRefused, nameof(ShiftSignup), signup.Id,
            $"shift '{signup.Shift.Rota.Name}'" + (reason is not null ? $": {reason}" : ""),
            reviewerUserId,
            signup.UserId, AuditSubjectTypes.User);

        await notices.DispatchSignupChangeNotificationAsync(signup, signup.Shift,
            "Shifts_Notification_ChangeRefused", signup.Shift.Rota.Name, signup.Shift.DayOffset);

        return SignupResult.Ok(signup.Id);
    }

    public async Task<SignupResult> BailAsync(Guid signupId, Guid actorUserId, string? reason)
    {
        var signup = await repo.GetByIdForMutationAsync(signupId);
        if (signup is null) return SignupResult.Fail(localizer["Shifts_SignupNotFound"]);

        var calendar = await calendarResolver.GetAsync(signup.Shift.Rota.EventSettingsId);
        if (calendar is null) return SignupResult.Fail(localizer["Shifts_EventCalendarNotConfigured"]);
        var now = clock.GetCurrentInstant();
        var isOwner = signup.UserId == actorUserId;
        var earlyEntryClosed = signup.Shift.IsEarlyEntry && calendar.IsEarlyEntryClosed(now);
        var isPrivileged = (!isOwner || earlyEntryClosed)
            && await IsPrivilegedAsync(actorUserId, signup.Shift.Rota.TeamId);

        if (!isOwner && !isPrivileged)
            return SignupResult.Fail(localizer["Shifts_Bail_NotAuthorized"]);

        if (signup.Status == SignupStatus.Bailed)
        {
            logger.LogWarning("Bail attempted on already-bailed signup {SignupId} by actor {ActorUserId}", signupId, actorUserId);
            return SignupResult.Fail(localizer["Shifts_Bail_AlreadyBailed"]);
        }

        if (signup.Status is not (SignupStatus.Pending or SignupStatus.Confirmed))
        {
            logger.LogWarning("Bail attempted on inactive signup {SignupId} in {Status} state by actor {ActorUserId}",
                signupId, signup.Status, actorUserId);
            return SignupResult.Fail(localizer["Shifts_Bail_NotActive"]);
        }

        if (earlyEntryClosed && !isPrivileged)
            return SignupResult.Fail(localizer["Shifts_Bail_EarlyEntryClosed"]);

        if (IsStatusReasonTooLong(reason, signup.Id, actorUserId))
            return SignupResult.Fail(localizer["Shifts_StatusReasonTooLong"]);

        var wasConfirmed = signup.Status == SignupStatus.Confirmed;
        signup.Bail(actorUserId, clock, reason);

        try
        {
            await repo.SaveChangesAsync();
        }
        finally
        {
            InvalidateSignupCaches(signup.Shift.Rota.EventSettingsId, signup.ShiftId);
        }

        await auditLogService.LogAsync(
            ShiftsAuditActions.ShiftSignupBailed, nameof(ShiftSignup), signup.Id,
            $"shift '{signup.Shift.Rota.Name}'" + (reason is not null ? $": {reason}" : ""),
            actorUserId,
            signup.UserId, AuditSubjectTypes.User);

        await notices.DispatchSignupChangeNotificationAsync(signup, signup.Shift,
            "Shifts_Notification_ChangeBailed", signup.Shift.Rota.Name, signup.Shift.DayOffset);

        if (wasConfirmed)
            await notices.CheckAndNotifyCoverageGapAsync(signup, signup.Shift);

        return SignupResult.Ok(signup.Id);
    }

    public async Task<SignupResult> VoluntellAsync(Guid userId, Guid shiftId, Guid enrollerUserId)
    {
        using var enrollment = await EnrollmentLock.AcquireAsync(logger);
        var targetUserId = await ResolveVoluntellTargetAsync(userId);
        if (targetUserId is null)
            return SignupResult.Fail(localizer["Shifts_AccountCannotSignUp"]);
        userId = targetUserId.Value;

        var activeShiftIds = await repo.GetActiveShiftIdsForUserAsync(userId, [shiftId]);
        if (activeShiftIds.Contains(shiftId))
            return SignupResult.Fail(localizer["Shifts_Signup_AlreadySignedUp"]);

        var shift = await repo.GetShiftAsync(shiftId, ShiftReadShape.Context);
        if (shift is null) return SignupResult.Fail(localizer["Shifts_Signup_ShiftNotFound"]);

        var calendar = await calendarResolver.GetAsync(shift.Rota.EventSettingsId);
        if (calendar is null) return SignupResult.Fail(localizer["Shifts_EventCalendarNotConfigured"]);
        var now = clock.GetCurrentInstant();

        var confirmedCount = shift.ShiftSignups.Count(d => d.Status == SignupStatus.Confirmed);
        if (confirmedCount >= shift.MaxVolunteers)
            return SignupResult.Fail(localizer["Shifts_Signup_AtCapacity"]);

        var overlapWarning = await signupSelection.CheckOverlapAsync(userId, shift, calendar);
        if (overlapWarning is not null)
            return SignupResult.Fail(overlapWarning);

        var signup = NewSignup(userId, shiftId, enrollerUserId, now, autoConfirm: true);
        signup.Enrolled = true;
        signup.EnrolledByUserId = enrollerUserId;

        repo.AddRange([signup]);

        try
        {
            await repo.SaveChangesAsync();
        }
        finally
        {
            InvalidateSignupCaches(calendar.Id, shiftId);
        }
        enrollment.Dispose();
        await auditLogService.LogAsync(
            ShiftsAuditActions.ShiftSignupVoluntold, nameof(ShiftSignup), signup.Id,
            $"shift '{shift.Rota.Name}' on {FormatAuditDay(calendar, shift.DayOffset)}",
            enrollerUserId,
            userId, AuditSubjectTypes.User);

        await notices.DispatchSignupChangeNotificationAsync(signup, shift,
            "Shifts_Notification_ChangeAssigned", shift.Rota.Name, shift.DayOffset);

        if (shift.GetAbsoluteEnd(calendar) > now)
        {
            await notices.SendAssignmentNoticeAsync(userId, signup.Id, "Shifts_Notification_AssignedDay", shift.Rota.Name, shift.DayOffset);
        }

        return SignupResult.Ok(signup.Id);
    }

    public async Task<SignupResult> VoluntellRangeAsync(Guid userId, Guid rotaId, int startDayOffset, int endDayOffset, Guid enrollerUserId)
    {
        using var enrollment = await EnrollmentLock.AcquireAsync(logger);
        var targetUserId = await ResolveVoluntellTargetAsync(userId);
        if (targetUserId is null)
            return SignupResult.Fail(localizer["Shifts_AccountCannotSignUp"]);
        userId = targetUserId.Value;

        var rota = await repo.GetRotaAsync(rotaId, RotaReadShape.Shifts);
        if (rota is null) return SignupResult.Fail(localizer["Shifts_Signup_RotaNotFound"]);

        var shiftsInRange = ShiftSignupSelection.SelectAllDayRangeShifts(rota, startDayOffset, endDayOffset);

        if (shiftsInRange.Count == 0)
            return SignupResult.Fail(localizer["Shifts_Signup_RangeEmpty"]);

        var shiftIdsInRange = shiftsInRange.Select(s => s.Id).ToHashSet();
        var existingShiftIds = await repo.GetActiveShiftIdsForUserAsync(userId, shiftIdsInRange);

        var shiftsToAssign = shiftsInRange
            .Where(s => !existingShiftIds.Contains(s.Id))
            .ToList();

        if (shiftsToAssign.Count == 0)
            return SignupResult.Fail(localizer["Shifts_Signup_RangeAlreadySignedUp"]);

        var calendar = await calendarResolver.GetAsync(rota.EventSettingsId);
        if (calendar is null) return SignupResult.Fail(localizer["Shifts_EventCalendarNotConfigured"]);
        var existingSignups = await signupSelection.GetActiveUserSignupsAsync(userId);
        var conflictingDays = await signupSelection.GetConflictingRangeDaysAsync(shiftsToAssign, existingSignups, calendar);
        var assignable = shiftsToAssign.Where(s => !conflictingDays.Contains(s.DayOffset)).ToList();
        var skippedOverlaps = shiftsToAssign.Count - assignable.Count;

        if (assignable.Count == 0)
            return SignupResult.Fail(localizer["Shifts_Signup_RangeConflicts"]);

        var assignableIds = assignable.Select(s => s.Id).ToHashSet();
        var signupCounts = await repo.GetConfirmedSignupCountsByShiftAsync(assignableIds);

        var skippedCapacity = new List<int>();
        var capacityFiltered = new List<Shift>();
        foreach (var shift in assignable)
        {
            if (signupCounts.GetValueOrDefault(shift.Id) >= shift.MaxVolunteers)
                skippedCapacity.Add(shift.DayOffset);
            else
                capacityFiltered.Add(shift);
        }

        if (capacityFiltered.Count == 0)
            return SignupResult.Fail(localizer["Shifts_Signup_RangeAtCapacity"]);

        assignable = capacityFiltered;

        var blockId = Guid.NewGuid();
        var now = clock.GetCurrentInstant();
        ShiftSignup? firstSignup = null;
        var warningParts = new List<string>();
        if (skippedOverlaps > 0)
            warningParts.Add($"Skipped {skippedOverlaps} shift(s) due to time conflicts.");
        if (skippedCapacity.Count > 0)
            warningParts.Add($"Skipped day(s) {string.Join(", ", skippedCapacity)} at capacity.");
        string? warning = warningParts.Count > 0 ? string.Join(" ", warningParts) : null;

        var voluntoldForAudit = new List<(ShiftSignup Signup, int DayOffset)>();
        foreach (var shift in assignable)
        {
            var signup = NewSignup(userId, shift.Id, enrollerUserId, now, autoConfirm: true, blockId: blockId);
            signup.Enrolled = true;
            signup.EnrolledByUserId = enrollerUserId;

            repo.AddRange([signup]);
            firstSignup ??= signup;
            voluntoldForAudit.Add((signup, shift.DayOffset));
        }

        try
        {
            await repo.SaveChangesAsync();
        }
        finally
        {
            InvalidateRangeSignupCaches(calendar.Id, rotaId);
        }
        enrollment.Dispose();
        foreach (var (auditedSignup, dayOffset) in voluntoldForAudit)
        {
            await auditLogService.LogAsync(
                ShiftsAuditActions.ShiftSignupVoluntold, nameof(ShiftSignup), auditedSignup.Id,
                $"'{rota.Name}' on {FormatAuditDay(calendar, dayOffset)} (range)",
                enrollerUserId,
                userId, AuditSubjectTypes.User);
        }

        await notices.DispatchSignupChangeNotificationAsync(firstSignup!, assignable[0], rota,
            "Shifts_Notification_ChangeAssignedRange", rota.Name, assignable.Count);

        if (assignable.Any(s => s.GetAbsoluteEnd(calendar) > now))
        {
            await notices.SendAssignmentNoticeAsync(userId, firstSignup!.Id, assignable.Count == 1
                ? "Shifts_Notification_AssignedRangeSingular" : "Shifts_Notification_AssignedRange", rota.Name, assignable.Count);
        }

        return SignupResult.Ok(firstSignup!.Id, warning);
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

        try
        {
            await repo.SaveChangesAsync();
        }
        finally
        {
            InvalidateSignupCaches(calendar.Id, signup.ShiftId);
        }

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

        try
        {
            await repo.SaveChangesAsync();
        }
        finally
        {
            InvalidateSignupCaches(signup.Shift.Rota.EventSettingsId, signup.ShiftId);
        }

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

    public async Task<SignupResult> SignUpRangeAsync(
        Guid userId,
        Guid rotaId,
        int startDayOffset,
        int endDayOffset,
        Guid? actorUserId = null,
        ShiftSignupRequestFlags flags = ShiftSignupRequestFlags.None)
    {
        using var enrollment = await EnrollmentLock.AcquireAsync(logger);
        var user = await users.GetUserInfoAsync(userId);
        if (user?.State != UserState.Active)
            return SignupResult.Fail(localizer["Shifts_AccountCannotSignUp"]);
        userId = user.Id;

        var isPrivileged = flags.HasFlag(ShiftSignupRequestFlags.Privileged);
        var skipConflicts = flags.HasFlag(ShiftSignupRequestFlags.SkipConflicts);
        var rota = await repo.GetRotaAsync(rotaId, RotaReadShape.EventSettings | RotaReadShape.Shifts);
        if (rota is null) return SignupResult.Fail(localizer["Shifts_Signup_RotaNotFound"]);

        var localEs = rota.EventSettings;
        var calendar = await calendarResolver.GetAsync(rota.EventSettingsId);
        if (calendar is null) return SignupResult.Fail(localizer["Shifts_EventCalendarNotConfigured"]);
        var now = clock.GetCurrentInstant();
        bool? canApprove = isPrivileged ? null : await IsPrivilegedAsync(userId, rota.TeamId);
        isPrivileged = isPrivileged || canApprove == true;

        if (!localEs.IsShiftBrowsingOpen && !isPrivileged)
            return SignupResult.Fail(localizer["Shifts_Signup_BrowsingClosed"]);

        if (!rota.IsVisibleToVolunteers && !isPrivileged)
            return SignupResult.Fail(localizer["Shifts_Signup_RangeRestricted"]);

        if (rota.Period == RotaPeriod.Build
            && calendar.IsEarlyEntryClosed(now)
            && !isPrivileged)
        {
            return SignupResult.Fail(localizer["Shifts_Signup_EarlyEntryClosed"]);
        }

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

        var autoConfirm = rota.Policy == SignupPolicy.Public
            || (canApprove ?? await IsPrivilegedAsync(userId, rota.TeamId));
        var createdSignups = StageRangeSignups(userId, actorUserId, blockId, now, availableShifts, autoConfirm);

        try
        {
            await repo.SaveChangesAsync();
        }
        finally
        {
            InvalidateRangeSignupCaches(calendar.Id, rotaId);
        }
        enrollment.Dispose();
        await AuditRangeSignupsAsync(userId, actorUserId ?? userId, rota, calendar, createdSignups.SignupsForAudit, autoConfirm);

        if (autoConfirm)
        {
            await notices.DispatchSignupChangeNotificationAsync(createdSignups.LastSignup, availableShifts[^1], rota,
                "Shifts_Notification_ChangeConfirmedRange", rota.Name, availableShifts.Count);
        }

        return SignupResult.Ok(createdSignups.LastSignup.Id, warning);
    }

    private void InvalidateSignupCaches(Guid eventSettingsId, Guid shiftId)
    {
        shiftMgmt.InvalidateDashboardCaches(eventSettingsId);
        viewInvalidator.InvalidateShift(shiftId);
    }

    private void InvalidateRangeSignupCaches(Guid eventSettingsId, Guid rotaId)
    {
        shiftMgmt.InvalidateDashboardCaches(eventSettingsId);
        // A range touches every shift in the rota.
        viewInvalidator.InvalidateRota(rotaId);
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

    private RangeSignupCreation StageRangeSignups(
        Guid userId,
        Guid? actorUserId,
        Guid blockId,
        Instant now,
        IReadOnlyList<Shift> availableShifts,
        bool autoConfirm)
    {
        ShiftSignup? lastSignup = null;
        var rangeSignupsForAudit = new List<(ShiftSignup Signup, int DayOffset)>();
        foreach (var shift in availableShifts)
        {
            var signup = NewSignup(userId, shift.Id, actorUserId, now, autoConfirm, blockId);

            repo.AddRange([signup]);
            lastSignup = signup;
            rangeSignupsForAudit.Add((signup, shift.DayOffset));
        }

        return new RangeSignupCreation(lastSignup!, rangeSignupsForAudit);
    }

    private async Task AuditRangeSignupsAsync(
        Guid userId,
        Guid actorUserId,
        Rota rota,
        BurnSettingsInfo eventSettings,
        IEnumerable<(ShiftSignup Signup, int DayOffset)> rangeSignupsForAudit,
        bool autoConfirm)
    {
        var statusSuffix = autoConfirm ? "confirmed" : "pending";
        foreach (var (auditedSignup, dayOffset) in rangeSignupsForAudit)
        {
            await auditLogService.LogAsync(
                ShiftsAuditActions.ShiftSignupCreated,
                nameof(ShiftSignup), auditedSignup.Id,
                $"'{rota.Name}' on {FormatAuditDay(eventSettings, dayOffset)} (range, {statusSuffix})",
                actorUserId,
                userId, AuditSubjectTypes.User);
        }
    }

    public async Task<SignupResult> ApproveRangeAsync(Guid signupBlockId, Guid reviewerUserId)
    {
        using var enrollment = await EnrollmentLock.AcquireAsync(logger);
        var signups = await repo.GetBlockForMutationAsync(
            signupBlockId,
            ShiftSignupBlockMutationScope.PendingOnly);

        if (signups.Count == 0) return SignupResult.Fail("No pending signups found for this block.");

        var warnings = new List<string>();
        var skippedAtCapacity = new List<ShiftSignup>();
        var now = clock.GetCurrentInstant();
        var approved = new List<ShiftSignup>();
        var calendar = await calendarResolver.GetAsync(signups[0].Shift.Rota.EventSettingsId);
        if (calendar is null) return SignupResult.Fail(localizer["Shifts_EventCalendarNotConfigured"]);

        var existingSignups = await signupSelection.GetActiveUserSignupsAsync(signups[0].UserId);
        var conflictingDays = await signupSelection.GetConflictingRangeDaysAsync(
            signups.Select(s => s.Shift).ToList(), existingSignups, calendar);
        if (conflictingDays.Count > 0)
            warnings.Add(localizer["Shifts_Signup_RangeConflictDays", ShiftSignupSelection.FormatRangeDayList(calendar, conflictingDays)].Value);

        foreach (var signup in signups)
        {
            var confirmedCount = signup.Shift.ShiftSignups.Count(d => d.Status == SignupStatus.Confirmed);
            if (confirmedCount >= signup.Shift.MaxVolunteers)
            {
                skippedAtCapacity.Add(signup);
                continue;
            }

            if (signup.Shift.IsEarlyEntry)
            {
                var eeWarning = await signupSelection.CheckEeCapAsync(calendar, signup.Shift.DayOffset);
                if (eeWarning is not null)
                    warnings.Add(eeWarning);
            }

            if (signup.Shift.IsEarlyEntry && calendar.IsEarlyEntryClosed(now))
            {
                var isPrivileged = await IsPrivilegedAsync(reviewerUserId, signup.Shift.Rota.TeamId);
                if (!isPrivileged)
                    return SignupResult.Fail("Cannot approve build shift signups after early entry close.");
            }

            signup.Confirm(reviewerUserId, clock);
            approved.Add(signup);
        }

        if (skippedAtCapacity.Count > 0)
        {
            foreach (var skipped in skippedAtCapacity)
            {
                skipped.Refuse(reviewerUserId, clock, "Shift at capacity");
            }

            var skippedDays = skippedAtCapacity.Select(s => s.Shift.DayOffset).ToList();
            warnings.Add($"Day(s) {string.Join(", ", skippedDays)} refused (at capacity).");
        }

        var cacheSignup = approved.Count > 0 ? approved[0] : skippedAtCapacity[0];
        try
        {
            await repo.SaveChangesAsync();
        }
        finally
        {
            InvalidateRangeSignupCaches(calendar.Id, cacheSignup.Shift.RotaId);
        }
        enrollment.Dispose();

        foreach (var approvedSignup in approved)
        {
            await auditLogService.LogAsync(
                ShiftsAuditActions.ShiftSignupConfirmed, nameof(ShiftSignup), approvedSignup.Id,
                $"shift '{approvedSignup.Shift.Rota.Name}' day {approvedSignup.Shift.DayOffset} (range)",
                reviewerUserId,
                approvedSignup.UserId, AuditSubjectTypes.User);
        }

        foreach (var skipped in skippedAtCapacity)
        {
            await auditLogService.LogAsync(
                ShiftsAuditActions.ShiftSignupRefused, nameof(ShiftSignup), skipped.Id,
                $"shift '{skipped.Shift.Rota.Name}' day {skipped.Shift.DayOffset} (auto-refused, at capacity)",
                reviewerUserId,
                skipped.UserId, AuditSubjectTypes.User);
        }

        if (approved.Count == 0)
            return SignupResult.Fail("Cannot approve: all shifts in this range are at capacity.");

        await notices.DispatchSignupChangeNotificationAsync(approved[0], approved[0].Shift,
            "Shifts_Notification_ChangeApprovedRange", approved[0].Shift.Rota.Name, approved.Count);

        var warning = warnings.Count > 0 ? string.Join(" ", warnings.Distinct(StringComparer.Ordinal)) : null;
        return SignupResult.Ok(approved[0].Id, warning);
    }

    public async Task<SignupResult> RefuseRangeAsync(Guid signupBlockId, Guid reviewerUserId, string? reason)
    {
        var signups = await repo.GetBlockForMutationAsync(
            signupBlockId,
            ShiftSignupBlockMutationScope.PendingOnly);

        if (signups.Count == 0) return SignupResult.Fail("No pending signups found for this block.");
        if (IsStatusReasonTooLong(reason, signups[0].Id, reviewerUserId))
            return SignupResult.Fail(localizer["Shifts_StatusReasonTooLong"]);

        foreach (var signup in signups)
        {
            signup.Refuse(reviewerUserId, clock, reason);
        }

        try
        {
            await repo.SaveChangesAsync();
        }
        finally
        {
            InvalidateRangeSignupCaches(signups[0].Shift.Rota.EventSettingsId, signups[0].Shift.RotaId);
        }

        foreach (var signup in signups)
        {
            await auditLogService.LogAsync(
                ShiftsAuditActions.ShiftSignupRefused, nameof(ShiftSignup), signup.Id,
                $"shift '{signup.Shift.Rota.Name}' day {signup.Shift.DayOffset} (range)" +
                (reason is not null ? $": {reason}" : ""),
                reviewerUserId,
                signup.UserId, AuditSubjectTypes.User);
        }

        await notices.DispatchSignupChangeNotificationAsync(signups[0], signups[0].Shift,
            "Shifts_Notification_ChangeRefusedRange", signups[0].Shift.Rota.Name, signups.Count);

        return SignupResult.Ok(signups[0].Id);
    }

    public async Task<BailRangeResult> BailRangeAsync(Guid signupBlockId, Guid actorUserId, string? reason = null)
    {
        var signups = await repo.GetBlockForMutationAsync(
            signupBlockId,
            ShiftSignupBlockMutationScope.PendingAndConfirmed);

        if (signups.Count == 0) return new();

        var firstSignup = signups[0];
        var calendar = await calendarResolver.GetAsync(firstSignup.Shift.Rota.EventSettingsId);
        if (calendar is null) return new("Shifts_EventCalendarNotConfigured");
        var now = clock.GetCurrentInstant();
        var isOwner = firstSignup.UserId == actorUserId;
        var earlyEntryClosed = signups.Any(s => s.Shift.IsEarlyEntry) && calendar.IsEarlyEntryClosed(now);
        var isPrivileged = (!isOwner || earlyEntryClosed)
            && await IsPrivilegedAsync(actorUserId, firstSignup.Shift.Rota.TeamId);

        if (!isOwner && !isPrivileged)
            return new("Shifts_BailRange_NotAuthorized");

        if (earlyEntryClosed && !isPrivileged)
            return new("Shifts_Bail_EarlyEntryClosed");

        if (IsStatusReasonTooLong(reason, firstSignup.Id, actorUserId))
            return new("Shifts_StatusReasonTooLong");

        // Captured before Bail gives Pending and Confirmed the same status: only confirmed ones reduce staffing.
        var confirmedSignups = signups.Where(s => s.Status == SignupStatus.Confirmed).ToList();
        foreach (var signup in signups)
        {
            signup.Bail(actorUserId, clock, reason);
        }

        try
        {
            await repo.SaveChangesAsync();
        }
        finally
        {
            InvalidateRangeSignupCaches(firstSignup.Shift.Rota.EventSettingsId, firstSignup.Shift.RotaId);
        }

        foreach (var signup in signups)
        {
            await auditLogService.LogAsync(
                ShiftsAuditActions.ShiftSignupBailed, nameof(ShiftSignup), signup.Id,
                $"shift '{signup.Shift.Rota.Name}' day {signup.Shift.DayOffset} (range)" +
                (reason is not null ? $": {reason}" : ""),
                actorUserId,
                signup.UserId, AuditSubjectTypes.User);
        }

        await notices.DispatchSignupChangeNotificationAsync(firstSignup, firstSignup.Shift,
            "Shifts_Notification_ChangeBailedRange", firstSignup.Shift.Rota.Name, signups.Count);

        foreach (var signup in confirmedSignups)
        {
            await notices.CheckAndNotifyCoverageGapAsync(signup, signup.Shift);
        }
        return new();
    }

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

    private async Task<Guid?> ResolveVoluntellTargetAsync(Guid userId)
    {
        var user = await users.GetUserInfoAsync(userId);
        return user is null || user.IsTombstone ? null : user.Id;
    }

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
        try
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
        finally
        {
            // Rota views also carry signup reasons removed by erasure.
            viewInvalidator.InvalidateAll();
        }
    }

    public Task<IReadOnlyList<CalendarFeedItem>> GetPublicItemsForWindowAsync(Instant from, Instant to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<CalendarFeedItem>>([]);

    public Task<IReadOnlyList<CalendarFeedItem>> GetCalendarItemsForUserAsync(Guid userId, CancellationToken ct) =>
        personalData.GetCalendarItemsForUserAsync(userId, ct);

    public async Task<IReadOnlyList<(Guid SignupId, Guid ShiftId)>> CancelActiveSignupsForUserAsync(
        Guid userId, string reason, CancellationToken ct = default)
    {
        var eventIds = await GetSignupEventIdsAsync([userId], ct);
        IReadOnlyList<(Guid SignupId, Guid ShiftId)>? cancelled = null;
        try
        {
            cancelled = await repo.CancelActiveSignupsForUserAsync(userId, reason, ct);
            return cancelled;
        }
        finally
        {
            if (cancelled is null)
            {
                // The save may have committed without returning its affected shift ids.
                viewInvalidator.InvalidateAll();
            }
            else
            {
                foreach (var (_, shiftId) in cancelled)
                    viewInvalidator.InvalidateShift(shiftId);
            }
            if (cancelled is null || cancelled.Count > 0)
                foreach (var eventId in eventIds)
                    shiftMgmt.InvalidateDashboardCaches(eventId);
        }
    }

    public async Task<int> DeleteAllForUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default)
    {
        await adminAuthorization.RequireCurrentUserIsAdminAsync(ct);
        var eventIds = await GetSignupEventIdsAsync(userIds, ct);
        int? deleted = null;
        try
        {
            deleted = await repo.DeleteAllForUsersAsync(userIds, ct);
            return deleted.Value;
        }
        finally
        {
            // A failed acknowledgement leaves the count unknown; affected shift ids are not returned.
            if (deleted != 0)
            {
                viewInvalidator.InvalidateAll();
                foreach (var eventId in eventIds)
                    shiftMgmt.InvalidateDashboardCaches(eventId);
            }
        }
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
        // Merge also rewrites reviewer/enroller identities, so failure may affect any event's activity rows.
        IReadOnlyList<Guid> eventIds = (await repo.GetAllEventSettingsAsync(ct)).Select(e => e.Id).ToList();
        int? movedCount = null;
        try
        {
            var reassigned = await repo.ReassignToUserAsync(sourceUserId, targetUserId, updatedAt, ct);
            movedCount = reassigned.Count;
            eventIds = reassigned.EventIds;
        }
        finally
        {
            if (movedCount != 0)
            {
                viewInvalidator.InvalidateAll();
                foreach (var eventId in eventIds)
                    shiftMgmt.InvalidateDashboardCaches(eventId);
            }
        }

        if (movedCount > 0)
        {
            await auditLogService.LogAsync(
                ShiftsAuditActions.ShiftSignupReassigned, AuditSubjectTypes.User, targetUserId,
                $"Reassigned {movedCount} shift signup(s) from merged source user {sourceUserId}",
                actorUserId,
                targetUserId, AuditSubjectTypes.User);
        }
    }

    private async Task<IReadOnlyList<Guid>> GetSignupEventIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct) =>
        (await repo.GetForUsersAsync(userIds, ct: ct))
            .Select(s => s.Shift.Rota.EventSettingsId).Distinct().ToList();

    private sealed record RangeSignupCreation(
        ShiftSignup LastSignup,
        List<(ShiftSignup Signup, int DayOffset)> SignupsForAudit);
}
