using Humans.Application.Extensions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Shifts;

/// <summary>
/// Manages the shift signup state machine. No caching decorator (§15 Option A).
/// </summary>
public sealed class ShiftSignupService(
    IShiftManagementRepository repo,
    IVolunteerTrackingRepository trackingRepo,
    IShiftManagementService shiftMgmt,
    IBurnSettingsService burnSettings,
    IAuditLogService auditLogService,
    INotificationService notificationService,
    IAdminAuthorizationService adminAuthorization,
    IShiftViewInvalidator viewInvalidator,
    IEarlyEntryInvalidator earlyEntryInvalidator,
    IServiceProvider serviceProvider,
    IClock clock,
    ILogger<ShiftSignupService> logger) : IShiftSignupService, IUserDataContributor, IUserMerge
{
    // Lazy-resolved for notification coordinator / team-name lookups.
    private ITeamServiceRead TeamService => serviceProvider.GetRequiredService<ITeamServiceRead>();

    public async Task<SignupResult> SignUpAsync(
        Guid userId,
        Guid shiftId,
        Guid? actorUserId = null,
        ShiftSignupRequestFlags flags = ShiftSignupRequestFlags.None)
    {
        var isPrivileged = flags.HasFlag(ShiftSignupRequestFlags.Privileged);
        var activeShiftIds = await repo.GetActiveShiftIdsForUserAsync(userId, [shiftId]);
        if (activeShiftIds.Contains(shiftId))
            return SignupResult.Fail("Already signed up for this shift.");

        var shift = await repo.GetShiftAsync(shiftId, ShiftReadShape.Context);
        if (shift is null) return SignupResult.Fail("Shift not found.");

        var es = shift.Rota.EventSettings;
        var now = clock.GetCurrentInstant();
        isPrivileged = isPrivileged || await IsPrivilegedAsync(userId, shift.Rota.TeamId);

        if (!es.IsShiftBrowsingOpen && !isPrivileged)
            return SignupResult.Fail("Shift browsing is not currently open.");

        if (shift.AdminOnly && !isPrivileged)
            return SignupResult.Fail("This shift is restricted to coordinators and admins.");

        if (shift.IsEarlyEntry && es.EarlyEntryClose.HasValue && now >= es.EarlyEntryClose.Value && !isPrivileged)
            return SignupResult.Fail("Early entry signups are closed.");

        var overlapWarning = await CheckOverlapAsync(userId, shift, es);
        if (overlapWarning is not null)
            return SignupResult.Fail(overlapWarning);

        string? warning = null;
        var confirmedCount = shift.ShiftSignups.Count(d => d.Status == SignupStatus.Confirmed);
        if (confirmedCount >= shift.MaxVolunteers)
            return SignupResult.Fail("This shift is at capacity.");

        if (shift.IsEarlyEntry)
        {
            var eeWarning = await CheckEeCapAsync(es, shift.DayOffset);
            if (eeWarning is not null)
                warning = warning is null ? eeWarning : $"{warning} {eeWarning}";
        }

        var canApprove = await shiftMgmt.CanApproveSignupsAsync(userId, shift.Rota.TeamId);

        // Public rotas auto-confirm at signup regardless of the volunteer's admission/consent
        // status; only RequireApproval rotas park signups as Pending for coordinator review.
        var autoConfirm = shift.Rota.Policy == SignupPolicy.Public || canApprove;

        var signup = new ShiftSignup
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ShiftId = shiftId,
            Status = autoConfirm ? SignupStatus.Confirmed : SignupStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };

        if (autoConfirm)
        {
            signup.ReviewedByUserId = actorUserId ?? userId;
            signup.ReviewedAt = now;
        }

        repo.AddRange([signup]);

        await repo.SaveChangesAsync();
        viewInvalidator.InvalidateUser(userId);
        viewInvalidator.InvalidateShift(shiftId);
        if (autoConfirm && shift.IsEarlyEntry)
            earlyEntryInvalidator.InvalidateUser(userId);

        var shiftDate = es.GateOpeningDate.PlusDays(shift.DayOffset).ToDisplayShiftDate();
        var statusSuffix = autoConfirm ? "confirmed" : "pending";
        await auditLogService.LogAsync(
            AuditAction.ShiftSignupCreated, nameof(ShiftSignup), signup.Id,
            $"shift '{shift.Rota.Name}' on {shiftDate} ({statusSuffix})",
            userId,
            userId, nameof(User));

        if (autoConfirm)
        {
            await DispatchSignupChangeNotificationAsync(signup, shift,
                $"New confirmed signup for '{shift.Rota.Name}' on day {shift.DayOffset}.");
        }

        return SignupResult.Ok(signup, warning);
    }

    public async Task<SignupResult> ApproveAsync(Guid signupId, Guid reviewerUserId)
    {
        var signup = await repo.GetByIdForMutationAsync(signupId);
        if (signup is null) return SignupResult.Fail("Signup not found.");

        if (signup.Status != SignupStatus.Pending)
            return SignupResult.Fail($"Cannot approve signup in {signup.Status} state.");

        var es = signup.Shift.Rota.EventSettings;

        var overlapWarning = await CheckOverlapAsync(signup.UserId, signup.Shift, es);
        string? warning = null;
        if (overlapWarning is not null)
            warning = $"Warning: {overlapWarning}";

        var confirmedCount = signup.Shift.ShiftSignups.Count(d => d.Status == SignupStatus.Confirmed);
        if (confirmedCount >= signup.Shift.MaxVolunteers)
            return SignupResult.Fail("Cannot approve: shift is at capacity.");

        if (signup.Shift.IsEarlyEntry)
        {
            var eeWarning = await CheckEeCapAsync(es, signup.Shift.DayOffset);
            if (eeWarning is not null)
                warning = warning is null ? $"Warning: {eeWarning}" : $"{warning} {eeWarning}";
        }

        var now = clock.GetCurrentInstant();
        if (signup.Shift.IsEarlyEntry && es.EarlyEntryClose.HasValue && now >= es.EarlyEntryClose.Value)
        {
            var isPrivileged = await IsPrivilegedAsync(reviewerUserId, signup.Shift.Rota.TeamId);
            if (!isPrivileged)
                return SignupResult.Fail("Cannot approve build shift signups after early entry close.");
        }

        signup.Confirm(reviewerUserId, clock);

        await repo.SaveChangesAsync();
        viewInvalidator.InvalidateUser(signup.UserId);
        viewInvalidator.InvalidateShift(signup.ShiftId);
        if (signup.Shift.IsEarlyEntry)
            earlyEntryInvalidator.InvalidateUser(signup.UserId);

        await auditLogService.LogAsync(
            AuditAction.ShiftSignupConfirmed, nameof(ShiftSignup), signup.Id,
            $"shift '{signup.Shift.Rota.Name}'",
            reviewerUserId,
            signup.UserId, nameof(User));

        await DispatchSignupChangeNotificationAsync(signup, signup.Shift,
            $"Signup approved for '{signup.Shift.Rota.Name}' on day {signup.Shift.DayOffset}.");

        return SignupResult.Ok(signup, warning);
    }

    public async Task<SignupResult> RefuseAsync(Guid signupId, Guid reviewerUserId, string? reason)
    {
        var signup = await repo.GetByIdForMutationAsync(signupId);
        if (signup is null) return SignupResult.Fail("Signup not found.");

        signup.Refuse(reviewerUserId, clock, reason);

        await repo.SaveChangesAsync();
        viewInvalidator.InvalidateUser(signup.UserId);
        viewInvalidator.InvalidateShift(signup.ShiftId);

        await auditLogService.LogAsync(
            AuditAction.ShiftSignupRefused, nameof(ShiftSignup), signup.Id,
            $"shift '{signup.Shift.Rota.Name}'" + (reason is not null ? $": {reason}" : ""),
            reviewerUserId,
            signup.UserId, nameof(User));

        await DispatchSignupChangeNotificationAsync(signup, signup.Shift,
            $"Signup refused for '{signup.Shift.Rota.Name}' on day {signup.Shift.DayOffset}.");

        return SignupResult.Ok(signup);
    }

    public async Task<SignupResult> BailAsync(Guid signupId, Guid actorUserId, string? reason)
    {
        var signup = await repo.GetByIdForMutationAsync(signupId);
        if (signup is null) return SignupResult.Fail("Signup not found.");

        var es = signup.Shift.Rota.EventSettings;
        var now = clock.GetCurrentInstant();
        var isOwner = signup.UserId == actorUserId;
        var isPrivileged = await IsPrivilegedAsync(actorUserId, signup.Shift.Rota.TeamId);

        // Auth: must be signup owner or privileged (dept coordinator/NoInfoAdmin/Admin)
        if (!isOwner && !isPrivileged)
            return SignupResult.Fail("Not authorized to bail this signup.");

        if (signup.Status == SignupStatus.Bailed)
        {
            logger.LogWarning("Bail attempted on already-bailed signup {SignupId} by actor {ActorUserId}", signupId, actorUserId);
            return SignupResult.Fail("This signup has already been bailed.");
        }

        if (signup.Shift.IsEarlyEntry && es.EarlyEntryClose.HasValue && now >= es.EarlyEntryClose.Value && !isPrivileged)
            return SignupResult.Fail("Cannot bail from build shifts after early entry close.");

        signup.Bail(actorUserId, clock, reason);

        await repo.SaveChangesAsync();
        viewInvalidator.InvalidateUser(signup.UserId);
        viewInvalidator.InvalidateShift(signup.ShiftId);
        if (signup.Shift.IsEarlyEntry)
            earlyEntryInvalidator.InvalidateUser(signup.UserId);

        await auditLogService.LogAsync(
            AuditAction.ShiftSignupBailed, nameof(ShiftSignup), signup.Id,
            $"shift '{signup.Shift.Rota.Name}'" + (reason is not null ? $": {reason}" : ""),
            actorUserId,
            signup.UserId, nameof(User));

        await DispatchSignupChangeNotificationAsync(signup, signup.Shift,
            $"Volunteer bailed from '{signup.Shift.Rota.Name}' on day {signup.Shift.DayOffset}.");

        await CheckAndNotifyCoverageGapAsync(signup, signup.Shift);

        return SignupResult.Ok(signup);
    }

    public async Task<SignupResult> VoluntellAsync(Guid userId, Guid shiftId, Guid enrollerUserId)
    {
        var activeShiftIds = await repo.GetActiveShiftIdsForUserAsync(userId, [shiftId]);
        if (activeShiftIds.Contains(shiftId))
            return SignupResult.Fail("Already signed up for this shift.");

        var shift = await repo.GetShiftAsync(shiftId, ShiftReadShape.Context);
        if (shift is null) return SignupResult.Fail("Shift not found.");

        var es = shift.Rota.EventSettings;
        var now = clock.GetCurrentInstant();

        var confirmedCount = shift.ShiftSignups.Count(d => d.Status == SignupStatus.Confirmed);
        if (confirmedCount >= shift.MaxVolunteers)
            return SignupResult.Fail("This shift is at capacity.");

        var overlapWarning = await CheckOverlapAsync(userId, shift, es);
        if (overlapWarning is not null)
            return SignupResult.Fail(overlapWarning);

        var signup = new ShiftSignup
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ShiftId = shiftId,
            Status = SignupStatus.Confirmed,
            Enrolled = true,
            EnrolledByUserId = enrollerUserId,
            ReviewedByUserId = enrollerUserId,
            ReviewedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        repo.AddRange([signup]);

        await repo.SaveChangesAsync();
        viewInvalidator.InvalidateUser(userId);
        viewInvalidator.InvalidateShift(shiftId);
        if (shift.IsEarlyEntry)
            earlyEntryInvalidator.InvalidateUser(userId);

        await auditLogService.LogAsync(
            AuditAction.ShiftSignupVoluntold, nameof(ShiftSignup), signup.Id,
            $"shift '{shift.Rota.Name}' on {es.GateOpeningDate.PlusDays(shift.DayOffset).ToDisplayShiftDate()}",
            enrollerUserId,
            userId, nameof(User));

        await DispatchSignupChangeNotificationAsync(signup, shift,
            $"Voluntold for '{shift.Rota.Name}' on day {shift.DayOffset}.");

        try
        {
            await notificationService.SendAsync(
                NotificationSource.ShiftAssigned,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                $"You were assigned to {shift.Rota.Name} on day {shift.DayOffset}",
                [userId],
                actionUrl: "/Shifts",
                actionLabel: "View shifts");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch ShiftAssigned notification for user {UserId} shift {ShiftId}", userId, shiftId);
        }

        return SignupResult.Ok(signup);
    }

    public async Task<SignupResult> VoluntellRangeAsync(Guid userId, Guid rotaId, int startDayOffset, int endDayOffset, Guid enrollerUserId)
    {
        var rota = await repo.GetRotaAsync(rotaId, RotaReadShape.EventSettings | RotaReadShape.Shifts);
        if (rota is null) return SignupResult.Fail("Rota not found.");

        var shiftsInRange = SelectAllDayRangeShifts(rota, startDayOffset, endDayOffset);

        if (shiftsInRange.Count == 0)
            return SignupResult.Fail("No shifts found in the specified date range.");

        var shiftIdsInRange = shiftsInRange.Select(s => s.Id).ToHashSet();
        var existingShiftIds = await repo.GetActiveShiftIdsForUserAsync(userId, shiftIdsInRange);

        var shiftsToAssign = shiftsInRange
            .Where(s => !existingShiftIds.Contains(s.Id))
            .ToList();

        if (shiftsToAssign.Count == 0)
            return SignupResult.Fail("Already signed up for all shifts in this range.");

        var es = rota.EventSettings;
        var skippedOverlaps = new List<string>();
        var assignable = new List<Shift>();
        foreach (var shift in shiftsToAssign)
        {
            var overlapWarning = await CheckOverlapAsync(userId, shift, es);
            if (overlapWarning is not null)
                skippedOverlaps.Add(overlapWarning);
            else
                assignable.Add(shift);
        }

        if (assignable.Count == 0)
            return SignupResult.Fail("All shifts in range have time conflicts with existing signups.");

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
            return SignupResult.Fail("All shifts in range are at capacity.");

        assignable = capacityFiltered;

        var blockId = Guid.NewGuid();
        var now = clock.GetCurrentInstant();
        ShiftSignup? firstSignup = null;
        var warningParts = new List<string>();
        if (skippedOverlaps.Count > 0)
            warningParts.Add($"Skipped {skippedOverlaps.Count} shift(s) due to time conflicts.");
        if (skippedCapacity.Count > 0)
            warningParts.Add($"Skipped day(s) {string.Join(", ", skippedCapacity)} at capacity.");
        string? warning = warningParts.Count > 0 ? string.Join(" ", warningParts) : null;

        var voluntoldForAudit = new List<(ShiftSignup Signup, int DayOffset)>();
        foreach (var shift in assignable)
        {
            var signup = new ShiftSignup
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ShiftId = shift.Id,
                SignupBlockId = blockId,
                Status = SignupStatus.Confirmed,
                Enrolled = true,
                EnrolledByUserId = enrollerUserId,
                ReviewedByUserId = enrollerUserId,
                ReviewedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            };

            repo.AddRange([signup]);
            firstSignup ??= signup;
            voluntoldForAudit.Add((signup, shift.DayOffset));
        }

        await repo.SaveChangesAsync();
        viewInvalidator.InvalidateUser(userId);
        // Range affects every shift in the rota; cascade via the rota.
        viewInvalidator.InvalidateRota(rotaId);
        if (assignable.Any(s => s.IsEarlyEntry))
            earlyEntryInvalidator.InvalidateUser(userId);

        foreach (var (auditedSignup, dayOffset) in voluntoldForAudit)
        {
            await auditLogService.LogAsync(
                AuditAction.ShiftSignupVoluntold, nameof(ShiftSignup), auditedSignup.Id,
                $"'{rota.Name}' on {es.GateOpeningDate.PlusDays(dayOffset).ToDisplayShiftDate()} (range)",
                enrollerUserId,
                userId, nameof(User));
        }

        await DispatchSignupChangeNotificationAsync(firstSignup!, assignable[0], rota,
            $"Voluntold range for '{rota.Name}' ({assignable.Count} shifts).");

        try
        {
            await notificationService.SendAsync(
                NotificationSource.ShiftAssigned,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                $"You were assigned to {rota.Name} ({assignable.Count} shifts)",
                [userId],
                actionUrl: "/Shifts",
                actionLabel: "View shifts");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch ShiftAssigned notification for user {UserId} rota {RotaId}", userId, rotaId);
        }

        return SignupResult.Ok(firstSignup!, warning);
    }

    public async Task<SignupResult> MarkNoShowAsync(Guid signupId, Guid reviewerUserId)
    {
        var signup = await repo.GetByIdForMutationAsync(signupId);
        if (signup is null) return SignupResult.Fail("Signup not found.");

        var es = signup.Shift.Rota.EventSettings;
        var shiftEnd = signup.Shift.GetAbsoluteEnd(es);
        var now = clock.GetCurrentInstant();

        if (now < shiftEnd)
            return SignupResult.Fail("Cannot mark no-show before the shift ends.");

        signup.MarkNoShow(reviewerUserId, clock);

        await repo.SaveChangesAsync();
        viewInvalidator.InvalidateUser(signup.UserId);
        viewInvalidator.InvalidateShift(signup.ShiftId);

        await auditLogService.LogAsync(
            AuditAction.ShiftSignupNoShow, nameof(ShiftSignup), signup.Id,
            $"shift '{signup.Shift.Rota.Name}'",
            reviewerUserId,
            signup.UserId, nameof(User));

        return SignupResult.Ok(signup);
    }

    public async Task<SignupResult> RemoveSignupAsync(Guid signupId, Guid removedByUserId, string? reason)
    {
        var signup = await repo.GetByIdForMutationAsync(signupId);
        if (signup is null) return SignupResult.Fail("Signup not found.");

        if (signup.Status != SignupStatus.Confirmed)
            return SignupResult.Fail($"Cannot remove signup in {signup.Status} state.");

        signup.Remove(removedByUserId, clock, reason);

        await repo.SaveChangesAsync();
        viewInvalidator.InvalidateUser(signup.UserId);
        viewInvalidator.InvalidateShift(signup.ShiftId);
        if (signup.Shift.IsEarlyEntry)
            earlyEntryInvalidator.InvalidateUser(signup.UserId);

        await auditLogService.LogAsync(
            AuditAction.ShiftSignupCancelled, nameof(ShiftSignup), signup.Id,
            $"shift '{signup.Shift.Rota.Name}'" +
            (reason is not null ? $": {reason}" : ""),
            removedByUserId,
            signup.UserId, nameof(User));

        await DispatchSignupChangeNotificationAsync(signup, signup.Shift,
            $"Removed from '{signup.Shift.Rota.Name}' on day {signup.Shift.DayOffset}.");
        await CheckAndNotifyCoverageGapAsync(signup, signup.Shift);

        return SignupResult.Ok(signup);
    }

    public async Task<SignupResult> SignUpRangeAsync(
        Guid userId,
        Guid rotaId,
        int startDayOffset,
        int endDayOffset,
        Guid? actorUserId = null,
        ShiftSignupRequestFlags flags = ShiftSignupRequestFlags.None)
    {
        var isPrivileged = flags.HasFlag(ShiftSignupRequestFlags.Privileged);
        var skipConflicts = flags.HasFlag(ShiftSignupRequestFlags.SkipConflicts);
        var rota = await repo.GetRotaAsync(rotaId, RotaReadShape.EventSettings | RotaReadShape.Shifts);
        if (rota is null) return SignupResult.Fail("Rota not found.");

        var es = rota.EventSettings;
        var now = clock.GetCurrentInstant();
        isPrivileged = isPrivileged || await IsPrivilegedAsync(userId, rota.TeamId);

        if (!es.IsShiftBrowsingOpen && !isPrivileged)
            return SignupResult.Fail("Shift browsing is not currently open.");

        if (rota.Period == RotaPeriod.Build && es.EarlyEntryClose.HasValue && now >= es.EarlyEntryClose.Value && !isPrivileged)
            return SignupResult.Fail("Early entry signups are closed.");

        var shiftsInRange = SelectAllDayRangeShifts(rota, startDayOffset, endDayOffset);

        if (!isPrivileged && shiftsInRange.Any(s => s.AdminOnly))
            return SignupResult.Fail("One or more shifts in this range are restricted to coordinators and admins.");

        // Fetched upfront — used for both duplicate-check and time-overlap check.
        var shiftIdsInRange = shiftsInRange.Select(s => s.Id).ToHashSet();
        var existingSignups = await GetActiveUserSignupsAsync(userId);
        var activeShiftIds = existingSignups
            .Where(s => shiftIdsInRange.Contains(s.ShiftId))
            .Select(s => s.ShiftId)
            .ToHashSet();

        var skipMessages = new List<string>();
        if (activeShiftIds.Count > 0)
        {
            if (!skipConflicts)
                return SignupResult.Fail("Already signed up for one or more shifts in this range.");

            var alreadySignedUpDays = shiftsInRange
                .Where(s => activeShiftIds.Contains(s.Id))
                .Select(s => s.DayOffset)
                .ToList();
            var dayList = string.Join(", ", alreadySignedUpDays.Select(offset =>
                es.GateOpeningDate.PlusDays(offset).ToDisplayShiftDate()));
            skipMessages.Add($"Already signed up for day(s): {dayList}.");

            shiftsInRange = shiftsInRange.Where(s => !activeShiftIds.Contains(s.Id)).ToList();
        }

        var conflictingDays = new List<int>();
        foreach (var shift in shiftsInRange)
        {
            var shiftStart = shift.GetAbsoluteStart(es);
            var shiftEnd = shift.GetAbsoluteEnd(es);

            foreach (var existing in existingSignups)
            {
                var existingEs = existing.Shift.Rota.EventSettings;
                var existingStart = existing.Shift.GetAbsoluteStart(existingEs);
                var existingEnd = existing.Shift.GetAbsoluteEnd(existingEs);

                if (shiftStart < existingEnd && shiftEnd > existingStart)
                {
                    conflictingDays.Add(shift.DayOffset);
                    break;
                }
            }
        }

        if (conflictingDays.Count > 0)
        {
            var dayList = string.Join(", ", conflictingDays.Select(offset =>
                es.GateOpeningDate.PlusDays(offset).ToDisplayShiftDate()));

            if (!skipConflicts)
                return SignupResult.Fail($"Time conflict on day(s): {dayList}.");

            skipMessages.Add($"Time conflict on day(s): {dayList}.");
            shiftsInRange = shiftsInRange.Where(s => !conflictingDays.Contains(s.DayOffset)).ToList();
        }

        if (shiftsInRange.Count == 0)
        {
            return skipMessages.Count > 0
                ? SignupResult.Fail(string.Join(" ", skipMessages) + " Nothing to add.")
                : SignupResult.Fail("No shifts found in the specified date range.");
        }

        shiftIdsInRange = shiftsInRange.Select(s => s.Id).ToHashSet();

        string? warning = skipMessages.Count > 0 ? string.Join(" ", skipMessages) : null;
        var signupCounts = await repo.GetConfirmedSignupCountsByShiftAsync(shiftIdsInRange);
        var fullDays = shiftsInRange
            .Where(s => signupCounts.GetValueOrDefault(s.Id) >= s.MaxVolunteers)
            .Select(s => s.DayOffset)
            .ToList();

        var availableShifts = shiftsInRange
            .Where(s => signupCounts.GetValueOrDefault(s.Id) < s.MaxVolunteers)
            .ToList();

        if (availableShifts.Count == 0)
            return SignupResult.Fail("All shifts in this range are at capacity.");

        if (fullDays.Count > 0)
        {
            var dayList = string.Join(", ", fullDays.Select(offset =>
                es.GateOpeningDate.PlusDays(offset).ToDisplayShiftDate()));
            var capacityWarning = $"Day(s) {dayList} are at capacity.";
            warning = warning is null ? capacityWarning : $"{warning} {capacityWarning}";
        }

        if (rota.Period == RotaPeriod.Build)
        {
            var fullEeDays = new List<int>();
            foreach (var dayOffset in availableShifts
                         .Where(shift => shift.IsEarlyEntry)
                         .Select(shift => shift.DayOffset)
                         .Distinct()
                         .OrderBy(day => day))
            {
                var eeWarning = await CheckEeCapAsync(es, dayOffset);
                if (eeWarning is not null)
                    fullEeDays.Add(dayOffset);
            }

            if (fullEeDays.Count > 0)
            {
                var eeDayList = string.Join(", ", fullEeDays.Select(offset =>
                    es.GateOpeningDate.PlusDays(offset).ToDisplayShiftDate()));
                var eeWarning = $"Early entry capacity reached for day(s): {eeDayList}.";
                warning = warning is null ? eeWarning : $"{warning} {eeWarning}";
            }
        }

        var blockId = Guid.NewGuid();

        // Public rotas auto-confirm at signup regardless of the volunteer's admission/consent
        // status; only RequireApproval rotas park signups as Pending for coordinator review.
        var autoConfirm = rota.Policy == SignupPolicy.Public ||
                          await shiftMgmt.CanApproveSignupsAsync(userId, rota.TeamId);
        ShiftSignup? lastSignup = null;

        var rangeSignupsForAudit = new List<(ShiftSignup Signup, int DayOffset)>();
        foreach (var shift in availableShifts)
        {
            var signup = new ShiftSignup
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ShiftId = shift.Id,
                SignupBlockId = blockId,
                Status = autoConfirm ? SignupStatus.Confirmed : SignupStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now
            };

            if (autoConfirm)
            {
                signup.ReviewedByUserId = actorUserId ?? userId;
                signup.ReviewedAt = now;
            }

            repo.AddRange([signup]);
            lastSignup = signup;
            rangeSignupsForAudit.Add((signup, shift.DayOffset));
        }

        await repo.SaveChangesAsync();
        viewInvalidator.InvalidateUser(userId);
        viewInvalidator.InvalidateRota(rotaId);
        if (autoConfirm && availableShifts.Any(s => s.IsEarlyEntry))
            earlyEntryInvalidator.InvalidateUser(userId);

        var statusSuffix = autoConfirm ? "confirmed" : "pending";
        foreach (var (auditedSignup, dayOffset) in rangeSignupsForAudit)
        {
            await auditLogService.LogAsync(
                AuditAction.ShiftSignupCreated,
                nameof(ShiftSignup), auditedSignup.Id,
                $"'{rota.Name}' on {es.GateOpeningDate.PlusDays(dayOffset).ToDisplayShiftDate()} (range, {statusSuffix})",
                userId,
                userId, nameof(User));
        }

        if (autoConfirm)
        {
            await DispatchSignupChangeNotificationAsync(lastSignup!, availableShifts[^1], rota,
                $"Range signup for '{rota.Name}' ({shiftsInRange.Count} shifts, confirmed).");
        }

        return SignupResult.Ok(lastSignup!, warning);
    }

    public async Task<SignupResult> ApproveRangeAsync(Guid signupBlockId, Guid reviewerUserId)
    {
        var signups = await repo.GetBlockForMutationAsync(
            signupBlockId,
            ShiftSignupBlockMutationScope.PendingOnly);

        if (signups.Count == 0) return SignupResult.Fail("No pending signups found for this block.");

        var warnings = new List<string>();
        var skippedAtCapacity = new List<ShiftSignup>();
        var now = clock.GetCurrentInstant();
        var approved = new List<ShiftSignup>();

        foreach (var signup in signups)
        {
            var es = signup.Shift.Rota.EventSettings;

            var confirmedCount = signup.Shift.ShiftSignups.Count(d => d.Status == SignupStatus.Confirmed);
            if (confirmedCount >= signup.Shift.MaxVolunteers)
            {
                skippedAtCapacity.Add(signup);
                continue;
            }

            if (signup.Shift.IsEarlyEntry)
            {
                var eeWarning = await CheckEeCapAsync(es, signup.Shift.DayOffset);
                if (eeWarning is not null)
                    warnings.Add(eeWarning);
            }

            if (signup.Shift.IsEarlyEntry && es.EarlyEntryClose.HasValue && now >= es.EarlyEntryClose.Value)
            {
                var isPrivileged = await IsPrivilegedAsync(reviewerUserId, signup.Shift.Rota.TeamId);
                if (!isPrivileged)
                    return SignupResult.Fail("Cannot approve build shift signups after early entry close.");
            }

            signup.Confirm(reviewerUserId, clock);
            if (signup.Shift.IsEarlyEntry)
                earlyEntryInvalidator.InvalidateUser(signup.UserId);
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

        if (approved.Count == 0)
        {
            if (skippedAtCapacity.Count > 0)
            {
                await repo.SaveChangesAsync();
                viewInvalidator.InvalidateUser(skippedAtCapacity[0].UserId);
                viewInvalidator.InvalidateRota(skippedAtCapacity[0].Shift.RotaId);

                foreach (var skipped in skippedAtCapacity)
                {
                    await auditLogService.LogAsync(
                        AuditAction.ShiftSignupRefused, nameof(ShiftSignup), skipped.Id,
                        $"shift '{skipped.Shift.Rota.Name}' day {skipped.Shift.DayOffset} (auto-refused, at capacity)",
                        reviewerUserId,
                        skipped.UserId, nameof(User));
                }
            }
            return SignupResult.Fail("Cannot approve: all shifts in this range are at capacity.");
        }

        await repo.SaveChangesAsync();
        viewInvalidator.InvalidateUser(approved[0].UserId);
        viewInvalidator.InvalidateRota(approved[0].Shift.RotaId);

        foreach (var approvedSignup in approved)
        {
            await auditLogService.LogAsync(
                AuditAction.ShiftSignupConfirmed, nameof(ShiftSignup), approvedSignup.Id,
                $"shift '{approvedSignup.Shift.Rota.Name}' day {approvedSignup.Shift.DayOffset} (range)",
                reviewerUserId,
                approvedSignup.UserId, nameof(User));
        }

        foreach (var skipped in skippedAtCapacity)
        {
            await auditLogService.LogAsync(
                AuditAction.ShiftSignupRefused, nameof(ShiftSignup), skipped.Id,
                $"shift '{skipped.Shift.Rota.Name}' day {skipped.Shift.DayOffset} (auto-refused, at capacity)",
                reviewerUserId,
                skipped.UserId, nameof(User));
        }

        await DispatchSignupChangeNotificationAsync(approved[0], approved[0].Shift,
            $"Range approved ({approved.Count} shifts) for '{approved[0].Shift.Rota.Name}'.");

        var warning = warnings.Count > 0 ? string.Join(" ", warnings.Distinct(StringComparer.Ordinal)) : null;
        return SignupResult.Ok(approved[0], warning);
    }

    public async Task<SignupResult> RefuseRangeAsync(Guid signupBlockId, Guid reviewerUserId, string? reason)
    {
        var signups = await repo.GetBlockForMutationAsync(
            signupBlockId,
            ShiftSignupBlockMutationScope.PendingOnly);

        if (signups.Count == 0) return SignupResult.Fail("No pending signups found for this block.");

        foreach (var signup in signups)
        {
            signup.Refuse(reviewerUserId, clock, reason);
        }

        await repo.SaveChangesAsync();
        viewInvalidator.InvalidateUser(signups[0].UserId);
        viewInvalidator.InvalidateRota(signups[0].Shift.RotaId);

        foreach (var signup in signups)
        {
            await auditLogService.LogAsync(
                AuditAction.ShiftSignupRefused, nameof(ShiftSignup), signup.Id,
                $"shift '{signup.Shift.Rota.Name}' day {signup.Shift.DayOffset} (range)" +
                (reason is not null ? $": {reason}" : ""),
                reviewerUserId,
                signup.UserId, nameof(User));
        }

        await DispatchSignupChangeNotificationAsync(signups[0], signups[0].Shift,
            $"Range refused ({signups.Count} shifts) for '{signups[0].Shift.Rota.Name}'.");

        return SignupResult.Ok(signups[0]);
    }

    public async Task BailRangeAsync(Guid signupBlockId, Guid actorUserId, string? reason = null)
    {
        var signups = await repo.GetBlockForMutationAsync(
            signupBlockId,
            ShiftSignupBlockMutationScope.PendingAndConfirmed);

        if (signups.Count == 0) return;

        var firstSignup = signups[0];
        var es = firstSignup.Shift.Rota.EventSettings;
        var now = clock.GetCurrentInstant();
        var isOwner = firstSignup.UserId == actorUserId;
        var isPrivileged = await IsPrivilegedAsync(actorUserId, firstSignup.Shift.Rota.TeamId);

        if (!isOwner && !isPrivileged)
            throw new InvalidOperationException("Not authorized to bail this signup block.");

        if (signups.Any(s => s.Shift.IsEarlyEntry) && es.EarlyEntryClose.HasValue && now >= es.EarlyEntryClose.Value && !isPrivileged)
            throw new InvalidOperationException("Cannot bail from build shifts after early entry close.");

        foreach (var signup in signups)
        {
            signup.Bail(actorUserId, clock, reason);
            if (signup.Shift.IsEarlyEntry)
                earlyEntryInvalidator.InvalidateUser(signup.UserId);
        }

        await repo.SaveChangesAsync();
        viewInvalidator.InvalidateUser(firstSignup.UserId);
        viewInvalidator.InvalidateRota(firstSignup.Shift.RotaId);

        foreach (var signup in signups)
        {
            await auditLogService.LogAsync(
                AuditAction.ShiftSignupBailed, nameof(ShiftSignup), signup.Id,
                $"shift '{signup.Shift.Rota.Name}' day {signup.Shift.DayOffset} (range)" +
                (reason is not null ? $": {reason}" : ""),
                actorUserId,
                signup.UserId, nameof(User));
        }

        await DispatchSignupChangeNotificationAsync(firstSignup, firstSignup.Shift,
            $"Range bail from '{firstSignup.Shift.Rota.Name}' ({signups.Count} shifts).");

        foreach (var signup in signups)
        {
            await CheckAndNotifyCoverageGapAsync(signup, signup.Shift);
        }
    }

    public Task<IReadOnlyList<ShiftSignup>> GetByUserAsync(Guid userId, Guid? eventSettingsId = null) =>
        repo.GetForUsersAsync([userId], eventSettingsId);

    public async Task<ShiftSignupTeamProbe?> GetTeamProbeAsync(Guid id, ShiftSignupTeamProbeScope scope)
    {
        var signup = await repo.GetTeamProbeAsync(id, scope);
        return signup is null
            ? null
            : new ShiftSignupTeamProbe(signup.Id, signup.ShiftId, signup.Shift.Rota.TeamId);
    }

    public async Task<IReadOnlyList<NoShowHistoryEntry>> GetNoShowHistoryAsync(Guid userId)
    {
        var signups = await repo.GetForUsersAsync([userId]);
        return signups
            .Where(s => s.Status == SignupStatus.NoShow)
            .OrderByDescending(s => s.ReviewedAt)
            .Select(s =>
            {
                var rota = s.Shift.Rota;
                var eventSettings = rota.EventSettings;
                return new NoShowHistoryEntry(
                    ShiftLabel: rota.Name,
                    TeamId: rota.TeamId,
                    ShiftStart: s.Shift.GetAbsoluteStart(eventSettings),
                    TimeZoneId: eventSettings.TimeZoneId,
                    ReviewedByUserId: s.ReviewedByUserId,
                    ReviewedAt: s.ReviewedAt);
            }).ToList();
    }

    private static List<Shift> SelectAllDayRangeShifts(Rota rota, int startDayOffset, int endDayOffset) =>
        rota.Shifts
            .Where(s => s.IsAllDay && s.DayOffset >= startDayOffset && s.DayOffset <= endDayOffset)
            .OrderBy(s => s.DayOffset)
            .ToList();

    private async Task<IReadOnlyList<ShiftSignup>> GetActiveUserSignupsAsync(Guid userId)
    {
        var signups = await repo.GetForUsersAsync([userId]);
        return signups
            .Where(IsActiveSignup)
            .ToList();
    }

    private static bool IsActiveSignup(ShiftSignup signup) =>
        signup.Status is SignupStatus.Pending or SignupStatus.Confirmed;

    private async Task<string?> CheckOverlapAsync(Guid userId, Shift targetShift, EventSettings es)
    {
        var targetStart = targetShift.GetAbsoluteStart(es);
        var targetEnd = targetShift.GetAbsoluteEnd(es);

        var userSignups = await GetActiveUserSignupsAsync(userId);

        IReadOnlyDictionary<Guid, string>? teamNames = null;

        foreach (var existing in userSignups)
        {
            if (existing.ShiftId == targetShift.Id) continue;
            if (existing.Status != SignupStatus.Confirmed) continue;

            var existingEs = existing.Shift.Rota.EventSettings;
            var existingStart = existing.Shift.GetAbsoluteStart(existingEs);
            var existingEnd = existing.Shift.GetAbsoluteEnd(existingEs);

            if (targetStart < existingEnd && targetEnd > existingStart)
            {
                var tz = DateTimeZoneProviders.Tzdb[existingEs.TimeZoneId];
                var dateStr = existingStart.InZone(tz).ToString("ddd MMM d HH:mm", null);

                if (teamNames is null)
                {
                    var teamsById = await TeamService.GetTeamsAsync();
                    teamNames = userSignups
                        .Select(s => s.Shift.Rota.TeamId)
                        .Distinct()
                        .Where(teamsById.ContainsKey)
                        .ToDictionary(id => id, id => teamsById[id].Name);
                }
                var teamName = teamNames.GetValueOrDefault(existing.Shift.Rota.TeamId, "Unknown");

                return $"Time conflict with '{existing.Shift.Rota.Name}' ({teamName}, {dateStr}).";
            }
        }

        return null;
    }

    private async Task<string?> CheckEeCapAsync(EventSettings es, int dayOffset)
    {
        var availableSlots = EarlyEntryCapacityCalculator.GetAvailableEeSlots(es, dayOffset);
        if (availableSlots <= 0)
            return "Early entry capacity reached for this day.";

        var currentEeCount = (await repo
            .GetUserIdsForDayAsync(es.Id, dayOffset, ShiftDayUserStatusScope.ConfirmedOnly))
            .Count;

        if (currentEeCount >= availableSlots)
            return "Early entry capacity reached.";

        return null;
    }

    private async Task<bool> IsPrivilegedAsync(Guid userId, Guid departmentTeamId)
    {
        return await shiftMgmt.CanApproveSignupsAsync(userId, departmentTeamId);
    }

    private async Task CheckAndNotifyCoverageGapAsync(ShiftSignup signup, Shift shift)
    {
        try
        {
            if (shift.MinVolunteers <= 0)
                return;

            var confirmedCount = shift.ShiftSignups.Count(s => s.Status == SignupStatus.Confirmed);
            if (confirmedCount >= shift.MinVolunteers)
                return;

            var teamId = shift.Rota.TeamId;
            var team = await TeamService.GetTeamAsync(teamId);
            var coordinatorIds = team?.Members
                .Where(m => m.Role == TeamMemberRole.Coordinator)
                .Select(m => m.UserId)
                .ToList() ?? [];

            if (coordinatorIds.Count == 0)
                return;

            await notificationService.SendAsync(
                NotificationSource.ShiftCoverageGap,
                NotificationClass.Actionable,
                NotificationPriority.High,
                $"Coverage gap: {shift.Rota.Name} day {shift.DayOffset}",
                coordinatorIds,
                body: $"Only {confirmedCount}/{shift.MinVolunteers} volunteers confirmed.",
                actionUrl: $"/Shifts?departmentId={teamId}",
                actionLabel: "Find cover →");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch ShiftCoverageGap notification for signup {SignupId}", signup.Id);
        }
    }

    private Task DispatchSignupChangeNotificationAsync(ShiftSignup signup, Shift shift, string changeDescription) =>
        DispatchSignupChangeNotificationAsync(signup, shift, shift.Rota, changeDescription);

    private async Task DispatchSignupChangeNotificationAsync(ShiftSignup signup, Shift shift, Rota rota, string changeDescription)
    {
        try
        {
            var teamId = rota.TeamId;
            var rotaName = rota.Name;

            var es = rota.EventSettings;
            var shiftDate = es.GateOpeningDate.PlusDays(shift.DayOffset);
            var enrichedDescription = $"{changeDescription} ({rotaName}, {shiftDate.ToDisplayShiftDate()})";

            var team = await TeamService.GetTeamAsync(teamId);
            var coordinatorIds = team?.Members
                .Where(m => m.Role == TeamMemberRole.Coordinator)
                .Select(m => m.UserId)
                .ToList() ?? [];

            if (coordinatorIds.Count == 0)
                return;

            await notificationService.SendAsync(
                NotificationSource.ShiftSignupChange,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                $"Shift signup change: {rotaName}",
                coordinatorIds,
                body: enrichedDescription,
                actionUrl: $"/Shifts?departmentId={teamId}",
                actionLabel: "View →");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch ShiftSignupChange notification for signup {SignupId}", signup.Id);
        }
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        // No `.Include(r => r.Team)` — Teams is a different section; resolve via ITeamService.
        var signups = (await repo.GetForUsersAsync([userId], ct: ct))
            .OrderByDescending(ss => ss.CreatedAt)
            .ToList();

        // GetTeamsAsync includes deactivated teams so historical signups still resolve a name (GDPR).
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

        // Resolve EventName for each distinct EventSettingsId via IBurnSettingsService
        // (EventSettings.EventSettings nav is not included by GetAvailabilityForUserAsync).
        var distinctEventSettingsIds = generalAvailability.Select(ga => ga.EventSettingsId).Distinct();
        var eventNamesById = new Dictionary<Guid, string>();
        foreach (var id in distinctEventSettingsIds)
        {
            var info = await burnSettings.GetByIdAsync(id, ct);
            if (info is not null)
                eventNamesById[id] = info.EventName;
        }

        var signupSlice = new UserDataSlice(GdprExportSections.ShiftSignups, signups.Select(ss => new
        {
            EventName = ss.Shift.Rota.EventSettings.EventName,
            Department = teamNamesById.TryGetValue(ss.Shift.Rota.TeamId, out var teamName) ? teamName : null,
            RotaName = ss.Shift.Rota.Name,
            ss.Shift.DayOffset,
            ss.Shift.IsAllDay,
            ss.Status,
            ss.Enrolled,
            ss.StatusReason,
            CreatedAt = ss.CreatedAt.ToInvariantInstantString(),
            ReviewedAt = ss.ReviewedAt.ToInvariantInstantString()
        }).ToList());

        // Dietary + medical moved to Profile — they are exported by the Profiles
        // data contributor now. The Shifts VEP slice carries only shift-matching data.
        var vepSlice = new UserDataSlice(GdprExportSections.VolunteerEventProfiles, volunteerEventProfiles.Select(vep => new
        {
            vep.Skills,
            vep.Quirks,
            vep.Languages,
            CreatedAt = vep.CreatedAt.ToInvariantInstantString(),
            UpdatedAt = vep.UpdatedAt.ToInvariantInstantString()
        }).ToList());

        var availabilitySlice = new UserDataSlice(GdprExportSections.GeneralAvailability, generalAvailability.Select(ga => new
        {
            EventName = eventNamesById.TryGetValue(ga.EventSettingsId, out var eventName) ? eventName : string.Empty,
            ga.AvailableDayOffsets,
            UpdatedAt = ga.UpdatedAt.ToInvariantInstantString()
        }).ToList());

        var tagPreferenceSlice = new UserDataSlice(GdprExportSections.ShiftTagPreferences, tagPreferences.Select(vtp => new
        {
            TagName = vtp.ShiftTag.Name
        }).ToList());

        return [signupSlice, vepSlice, availabilitySlice, tagPreferenceSlice];
    }

    public async Task<IReadOnlyList<(Guid SignupId, Guid ShiftId)>> CancelActiveSignupsForUserAsync(
        Guid userId, string reason, CancellationToken ct = default)
    {
        var cancelled = await repo.CancelActiveSignupsForUserAsync(userId, reason, ct);
        viewInvalidator.InvalidateUser(userId);
        foreach (var (_, shiftId) in cancelled)
            viewInvalidator.InvalidateShift(shiftId);
        earlyEntryInvalidator.InvalidateUser(userId);
        return cancelled;
    }

    public async Task<int> DeleteAllForUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default)
    {
        await adminAuthorization.RequireCurrentUserIsAdminAsync(ct);
        var deleted = await repo.DeleteAllForUsersAsync(userIds, ct);
        // Repo doesn't return affected shift ids; cheap at ~500-user scale to drop all and lazy-rebuild.
        if (deleted > 0)
            viewInvalidator.InvalidateAll();
        else
        {
            foreach (var userId in userIds)
                viewInvalidator.InvalidateUser(userId);
        }
        foreach (var userId in userIds)
            earlyEntryInvalidator.InvalidateUser(userId);
        return deleted;
    }

    public async Task<IReadOnlyList<OrphanSignupSnapshot>> GetAllForOrphanScanAsync(CancellationToken ct = default)
    {
        var signups = await repo.GetAllForOrphanScanAsync(ct);
        return signups.Select(s => new OrphanSignupSnapshot(
            s.Id,
            s.UserId,
            s.Shift.Rota.Name,
            s.Shift.Rota.EventSettings.GateOpeningDate.PlusDays(s.Shift.DayOffset),
            s.Status,
            s.CreatedAt,
            s.ReviewedByUserId,
            s.EnrolledByUserId,
            s.SignupBlockId)).ToList();
    }

    public Task<IReadOnlySet<Guid>> GetActiveCommittedUserIdsForEventAsync(
        Guid eventSettingsId, CancellationToken ct = default) =>
        repo.GetActiveCommittedUserIdsForEventAsync(eventSettingsId, ct);

    public async Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken ct)
    {
        var movedCount = await repo.ReassignToUserAsync(sourceUserId, targetUserId, updatedAt, ct);

        viewInvalidator.InvalidateUser(sourceUserId);
        viewInvalidator.InvalidateUser(targetUserId);
        // Affected shifts unknown; cheap to drop all at ~500-user scale.
        if (movedCount > 0)
            viewInvalidator.InvalidateAll();

        if (movedCount > 0)
        {
            earlyEntryInvalidator.InvalidateUser(sourceUserId);
            earlyEntryInvalidator.InvalidateUser(targetUserId);

            await auditLogService.LogAsync(
                AuditAction.ShiftSignupReassigned, nameof(User), targetUserId,
                $"Reassigned {movedCount} shift signup(s) from merged source user {sourceUserId}",
                actorUserId,
                targetUserId, nameof(User));
        }
    }
}
