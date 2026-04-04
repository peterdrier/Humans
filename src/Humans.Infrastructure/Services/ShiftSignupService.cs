using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Manages the shift signup state machine with invariant enforcement.
/// </summary>
public class ShiftSignupService : IShiftSignupService
{
    private readonly HumansDbContext _dbContext;
    private readonly IShiftManagementService _shiftMgmt;
    private readonly IAuditLogService _auditLogService;
    private readonly INotificationService _notificationService;
    private readonly IClock _clock;
    private readonly ILogger<ShiftSignupService> _logger;

    public ShiftSignupService(
        HumansDbContext dbContext,
        IShiftManagementService shiftMgmt,
        IAuditLogService auditLogService,
        INotificationService notificationService,
        IClock clock,
        ILogger<ShiftSignupService> logger)
    {
        _dbContext = dbContext;
        _shiftMgmt = shiftMgmt;
        _auditLogService = auditLogService;
        _notificationService = notificationService;
        _clock = clock;
        _logger = logger;
    }

    public async Task<SignupResult> SignUpAsync(Guid userId, Guid shiftId, Guid? actorUserId = null, bool isPrivileged = false)
    {
        // Prevent duplicate signups for the same shift
        var existingSignup = await _dbContext.ShiftSignups
            .AnyAsync(s => s.UserId == userId && s.ShiftId == shiftId &&
                           (s.Status == SignupStatus.Pending || s.Status == SignupStatus.Confirmed));
        if (existingSignup)
            return SignupResult.Fail("Already signed up for this shift.");

        var shift = await _dbContext.Shifts
            .Include(s => s.Rota).ThenInclude(r => r.EventSettings)
            .Include(s => s.Rota).ThenInclude(r => r.Team)
            .Include(s => s.ShiftSignups)
            .FirstOrDefaultAsync(s => s.Id == shiftId);

        if (shift is null) return SignupResult.Fail("Shift not found.");

        var es = shift.Rota.EventSettings;
        var now = _clock.GetCurrentInstant();
        isPrivileged = isPrivileged || await IsPrivilegedAsync(userId, shift.Rota.TeamId);

        // System open check
        if (!es.IsShiftBrowsingOpen && !isPrivileged)
            return SignupResult.Fail("Shift browsing is not currently open.");

        // AdminOnly check
        if (shift.AdminOnly && !isPrivileged)
            return SignupResult.Fail("This shift is restricted to coordinators and admins.");

        // EE freeze check for build shifts
        if (shift.IsEarlyEntry && es.EarlyEntryClose.HasValue && now >= es.EarlyEntryClose.Value && !isPrivileged)
            return SignupResult.Fail("Early entry signups are closed.");

        // Overlap check
        var overlapWarning = await CheckOverlapAsync(userId, shift, es);
        if (overlapWarning is not null)
            return SignupResult.Fail(overlapWarning);

        // Capacity warning
        string? warning = null;
        var confirmedCount = shift.ShiftSignups.Count(d => d.Status == SignupStatus.Confirmed);
        if (confirmedCount >= shift.MaxVolunteers)
            warning = "This shift is at capacity.";

        // EE cap warning
        if (shift.IsEarlyEntry)
        {
            var eeWarning = await CheckEeCapAsync(es, shift.DayOffset);
            if (eeWarning is not null)
                warning = warning is null ? eeWarning : $"{warning} {eeWarning}";
        }

        // Determine initial status — Admin, NoInfoAdmin, and Dept Coordinators auto-confirm
        var canApprove = await _shiftMgmt.CanApproveSignupsAsync(userId, shift.Rota.TeamId);
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

        _dbContext.ShiftSignups.Add(signup);

        if (autoConfirm)
        {
            await _auditLogService.LogAsync(
                AuditAction.ShiftSignupConfirmed, nameof(ShiftSignup), signup.Id,
                $"Auto-confirmed signup for shift '{shift.Rota.Name}'",
                userId);
        }

        await _dbContext.SaveChangesAsync();

        if (autoConfirm)
        {
            await DispatchSignupChangeNotificationAsync(signup,
                $"New confirmed signup for '{shift.Rota.Name}' on day {shift.DayOffset}.");
        }

        return SignupResult.Ok(signup, warning);
    }

    public async Task<SignupResult> ApproveAsync(Guid signupId, Guid reviewerUserId)
    {
        var signup = await GetSignupWithShiftAsync(signupId);
        if (signup is null) return SignupResult.Fail("Signup not found.");

        if (signup.Status != SignupStatus.Pending)
            return SignupResult.Fail($"Cannot approve signup in {signup.Status} state.");

        var es = signup.Shift.Rota.EventSettings;

        // Re-validate invariants
        var overlapWarning = await CheckOverlapAsync(signup.UserId, signup.Shift, es);
        string? warning = null;
        if (overlapWarning is not null)
            warning = $"Warning: {overlapWarning}";

        var confirmedCount = signup.Shift.ShiftSignups.Count(d => d.Status == SignupStatus.Confirmed);
        if (confirmedCount >= signup.Shift.MaxVolunteers)
            warning = warning is null ? "Warning: shift is at capacity." : $"{warning} Shift is at capacity.";

        // EE cap revalidation for build shifts
        if (signup.Shift.IsEarlyEntry)
        {
            var eeWarning = await CheckEeCapAsync(es, signup.Shift.DayOffset);
            if (eeWarning is not null)
                warning = warning is null ? $"Warning: {eeWarning}" : $"{warning} {eeWarning}";
        }

        // EE freeze check — block approval of build shifts after early entry close
        var now = _clock.GetCurrentInstant();
        if (signup.Shift.IsEarlyEntry && es.EarlyEntryClose.HasValue && now >= es.EarlyEntryClose.Value)
        {
            var isPrivileged = await IsPrivilegedAsync(reviewerUserId, signup.Shift.Rota.TeamId);
            if (!isPrivileged)
                return SignupResult.Fail("Cannot approve build shift signups after early entry close.");
        }

        signup.Confirm(reviewerUserId, _clock);

        await _auditLogService.LogAsync(
            AuditAction.ShiftSignupConfirmed, nameof(ShiftSignup), signup.Id,
            $"Approved signup for shift '{signup.Shift.Rota.Name}'",
            reviewerUserId);

        await _dbContext.SaveChangesAsync();

        await DispatchSignupChangeNotificationAsync(signup,
            $"Signup approved for '{signup.Shift.Rota.Name}' on day {signup.Shift.DayOffset}.");

        return SignupResult.Ok(signup, warning);
    }

    public async Task<SignupResult> RefuseAsync(Guid signupId, Guid reviewerUserId, string? reason)
    {
        var signup = await GetSignupWithShiftAsync(signupId);
        if (signup is null) return SignupResult.Fail("Signup not found.");

        signup.Refuse(reviewerUserId, _clock, reason);

        await _auditLogService.LogAsync(
            AuditAction.ShiftSignupRefused, nameof(ShiftSignup), signup.Id,
            $"Refused signup for shift '{signup.Shift.Rota.Name}'" + (reason is not null ? $": {reason}" : ""),
            reviewerUserId);

        await _dbContext.SaveChangesAsync();

        await DispatchSignupChangeNotificationAsync(signup,
            $"Signup refused for '{signup.Shift.Rota.Name}' on day {signup.Shift.DayOffset}.");

        return SignupResult.Ok(signup);
    }

    public async Task<SignupResult> BailAsync(Guid signupId, Guid actorUserId, string? reason)
    {
        var signup = await GetSignupWithShiftAsync(signupId);
        if (signup is null) return SignupResult.Fail("Signup not found.");

        var es = signup.Shift.Rota.EventSettings;
        var now = _clock.GetCurrentInstant();
        var isOwner = signup.UserId == actorUserId;
        var isPrivileged = await IsPrivilegedAsync(actorUserId, signup.Shift.Rota.TeamId);

        // Authorization: must be signup owner or privileged (dept coordinator/NoInfoAdmin/Admin)
        if (!isOwner && !isPrivileged)
            return SignupResult.Fail("Not authorized to bail this signup.");

        // EE freeze check for build shifts
        if (signup.Shift.IsEarlyEntry && es.EarlyEntryClose.HasValue && now >= es.EarlyEntryClose.Value && !isPrivileged)
            return SignupResult.Fail("Cannot bail from build shifts after early entry close.");

        signup.Bail(actorUserId, _clock, reason);

        await _auditLogService.LogAsync(
            AuditAction.ShiftSignupBailed, nameof(ShiftSignup), signup.Id,
            $"Bailed from shift '{signup.Shift.Rota.Name}'" + (reason is not null ? $": {reason}" : ""),
            actorUserId);

        await _dbContext.SaveChangesAsync();

        await DispatchSignupChangeNotificationAsync(signup,
            $"Volunteer bailed from '{signup.Shift.Rota.Name}' on day {signup.Shift.DayOffset}.");

        // Check for coverage gap after bail
        await CheckAndNotifyCoverageGapAsync(signup);

        return SignupResult.Ok(signup);
    }

    public async Task<SignupResult> VoluntellAsync(Guid userId, Guid shiftId, Guid enrollerUserId)
    {
        // Prevent duplicate signups for the same shift
        var existingSignup = await _dbContext.ShiftSignups
            .AnyAsync(s => s.UserId == userId && s.ShiftId == shiftId &&
                           (s.Status == SignupStatus.Pending || s.Status == SignupStatus.Confirmed));
        if (existingSignup)
            return SignupResult.Fail("Already signed up for this shift.");

        var shift = await _dbContext.Shifts
            .Include(s => s.Rota).ThenInclude(r => r.EventSettings)
            .Include(s => s.ShiftSignups)
            .FirstOrDefaultAsync(s => s.Id == shiftId);

        if (shift is null) return SignupResult.Fail("Shift not found.");

        var es = shift.Rota.EventSettings;
        var now = _clock.GetCurrentInstant();

        // Overlap check
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

        _dbContext.ShiftSignups.Add(signup);

        await _auditLogService.LogAsync(
            AuditAction.ShiftSignupVoluntold, nameof(ShiftSignup), signup.Id,
            $"Voluntold for shift '{shift.Rota.Name}'",
            enrollerUserId,
            userId, nameof(User));

        await _dbContext.SaveChangesAsync();

        await DispatchSignupChangeNotificationAsync(signup,
            $"Voluntold for '{shift.Rota.Name}' on day {shift.DayOffset}.");

        return SignupResult.Ok(signup);
    }

    public async Task<SignupResult> VoluntellRangeAsync(Guid userId, Guid rotaId, int startDayOffset, int endDayOffset, Guid enrollerUserId)
    {
        var rota = await _dbContext.Rotas
            .Include(r => r.EventSettings)
            .Include(r => r.Shifts)
            .FirstOrDefaultAsync(r => r.Id == rotaId);

        if (rota is null) return SignupResult.Fail("Rota not found.");

        // Find all-day shifts in the range
        var shiftsInRange = rota.Shifts
            .Where(s => s.IsAllDay && s.DayOffset >= startDayOffset && s.DayOffset <= endDayOffset)
            .OrderBy(s => s.DayOffset)
            .ToList();

        if (shiftsInRange.Count == 0)
            return SignupResult.Fail("No shifts found in the specified date range.");

        // Check for existing signups to skip (Confirmed or Pending)
        var shiftIdsInRange = shiftsInRange.Select(s => s.Id).ToHashSet();
        var existingShiftIds = await _dbContext.ShiftSignups
            .Where(s => s.UserId == userId && shiftIdsInRange.Contains(s.ShiftId) &&
                         (s.Status == SignupStatus.Pending || s.Status == SignupStatus.Confirmed))
            .Select(s => s.ShiftId)
            .ToHashSetAsync();

        var shiftsToAssign = shiftsInRange
            .Where(s => !existingShiftIds.Contains(s.Id))
            .ToList();

        if (shiftsToAssign.Count == 0)
            return SignupResult.Fail("Already signed up for all shifts in this range.");

        // Overlap check — skip shifts that conflict with existing confirmed signups in other rotas
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

        // Create confirmed signups with shared SignupBlockId
        var blockId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        ShiftSignup? firstSignup = null;
        string? warning = skippedOverlaps.Count > 0
            ? $"Skipped {skippedOverlaps.Count} shift(s) due to time conflicts."
            : null;

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

            _dbContext.ShiftSignups.Add(signup);
            firstSignup ??= signup;

            await _auditLogService.LogAsync(
                AuditAction.ShiftSignupVoluntold, nameof(ShiftSignup), signup.Id,
                $"Voluntold range for '{rota.Name}' day {shift.DayOffset} (block {blockId})",
                enrollerUserId,
                userId, nameof(User));
        }

        await _dbContext.SaveChangesAsync();

        await DispatchSignupChangeNotificationAsync(firstSignup!,
            $"Voluntold range for '{rota.Name}' ({assignable.Count} shifts).");

        return SignupResult.Ok(firstSignup!, warning);
    }

    public async Task<SignupResult> MarkNoShowAsync(Guid signupId, Guid reviewerUserId)
    {
        var signup = await GetSignupWithShiftAsync(signupId);
        if (signup is null) return SignupResult.Fail("Signup not found.");

        var es = signup.Shift.Rota.EventSettings;
        var shiftEnd = signup.Shift.GetAbsoluteEnd(es);
        var now = _clock.GetCurrentInstant();

        if (now < shiftEnd)
            return SignupResult.Fail("Cannot mark no-show before the shift ends.");

        signup.MarkNoShow(reviewerUserId, _clock);

        await _auditLogService.LogAsync(
            AuditAction.ShiftSignupNoShow, nameof(ShiftSignup), signup.Id,
            $"Marked no-show for shift '{signup.Shift.Rota.Name}'",
            reviewerUserId);

        await _dbContext.SaveChangesAsync();

        return SignupResult.Ok(signup);
    }

    public async Task<SignupResult> RemoveSignupAsync(Guid signupId, Guid removedByUserId, string? reason)
    {
        var signup = await GetSignupWithShiftAsync(signupId);
        if (signup is null) return SignupResult.Fail("Signup not found.");

        if (signup.Status != SignupStatus.Confirmed)
            return SignupResult.Fail($"Cannot remove signup in {signup.Status} state.");

        signup.Remove(removedByUserId, _clock, reason);

        await _auditLogService.LogAsync(
            AuditAction.ShiftSignupCancelled, nameof(ShiftSignup), signup.Id,
            $"Removed from shift '{signup.Shift.Rota.Name}'" +
            (reason is not null ? $": {reason}" : ""),
            removedByUserId);

        await _dbContext.SaveChangesAsync();

        await DispatchSignupChangeNotificationAsync(signup,
            $"Removed from '{signup.Shift.Rota.Name}' on day {signup.Shift.DayOffset}.");
        await CheckAndNotifyCoverageGapAsync(signup);

        return SignupResult.Ok(signup);
    }

    public async Task<SignupResult> SignUpRangeAsync(Guid userId, Guid rotaId, int startDayOffset, int endDayOffset, Guid? actorUserId = null, bool isPrivileged = false)
    {
        var rota = await _dbContext.Rotas
            .Include(r => r.EventSettings)
            .Include(r => r.Shifts)
            .FirstOrDefaultAsync(r => r.Id == rotaId);

        if (rota is null) return SignupResult.Fail("Rota not found.");

        var es = rota.EventSettings;
        var now = _clock.GetCurrentInstant();
        isPrivileged = isPrivileged || await IsPrivilegedAsync(userId, rota.TeamId);

        // System open check
        if (!es.IsShiftBrowsingOpen && !isPrivileged)
            return SignupResult.Fail("Shift browsing is not currently open.");

        // EE freeze check for build rotas
        if (rota.Period == RotaPeriod.Build && es.EarlyEntryClose.HasValue && now >= es.EarlyEntryClose.Value && !isPrivileged)
            return SignupResult.Fail("Early entry signups are closed.");

        // Find all-day shifts in the range
        var shiftsInRange = rota.Shifts
            .Where(s => s.IsAllDay && s.DayOffset >= startDayOffset && s.DayOffset <= endDayOffset)
            .OrderBy(s => s.DayOffset)
            .ToList();

        if (shiftsInRange.Count == 0)
            return SignupResult.Fail("No shifts found in the specified date range.");

        // AdminOnly check (Fix #2)
        if (!isPrivileged && shiftsInRange.Any(s => s.AdminOnly))
            return SignupResult.Fail("One or more shifts in this range are restricted to coordinators and admins.");

        // Duplicate signup check — reject if user already has Pending/Confirmed on any shift in range (Fix #1)
        var shiftIdsInRange = shiftsInRange.Select(s => s.Id).ToHashSet();
        var hasDuplicate = await _dbContext.ShiftSignups
            .AnyAsync(s => s.UserId == userId && shiftIdsInRange.Contains(s.ShiftId) &&
                           (s.Status == SignupStatus.Pending || s.Status == SignupStatus.Confirmed));
        if (hasDuplicate)
            return SignupResult.Fail("Already signed up for one or more shifts in this range.");

        // Check overlap for each day (include Pending signups too)
        var existingSignups = await _dbContext.ShiftSignups
            .Include(s => s.Shift).ThenInclude(s => s.Rota).ThenInclude(r => r.EventSettings)
            .Where(s => s.UserId == userId &&
                        (s.Status == SignupStatus.Confirmed || s.Status == SignupStatus.Pending))
            .ToListAsync();

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
            var dayList = string.Join(", ", conflictingDays);
            return SignupResult.Fail($"Time conflict on day(s): {dayList}.");
        }

        // Capacity check — warn if any shift is at/over capacity (Fix #4)
        string? warning = null;
        var signupCounts = await _dbContext.ShiftSignups
            .Where(s => shiftIdsInRange.Contains(s.ShiftId) && s.Status == SignupStatus.Confirmed)
            .GroupBy(s => s.ShiftId)
            .Select(g => new { ShiftId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.ShiftId, g => g.Count);
        var fullDays = shiftsInRange
            .Where(s => signupCounts.GetValueOrDefault(s.Id) >= s.MaxVolunteers)
            .Select(s => s.DayOffset)
            .ToList();
        if (fullDays.Count > 0)
            warning = $"Day(s) {string.Join(", ", fullDays)} are at capacity.";

        // EE cap check for build shifts
        if (rota.Period == RotaPeriod.Build)
        {
            var fullEeDays = new List<int>();
            foreach (var dayOffset in shiftsInRange
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
                var eeWarning = $"Early entry capacity reached for day(s): {string.Join(", ", fullEeDays)}.";
                warning = warning is null ? eeWarning : $"{warning} {eeWarning}";
            }
        }

        // Create signups
        var blockId = Guid.NewGuid();
        var autoConfirm = rota.Policy == SignupPolicy.Public ||
                          await _shiftMgmt.CanApproveSignupsAsync(userId, rota.TeamId);
        ShiftSignup? lastSignup = null;

        foreach (var shift in shiftsInRange)
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

            _dbContext.ShiftSignups.Add(signup);
            lastSignup = signup;

            if (autoConfirm)
            {
                await _auditLogService.LogAsync(
                    AuditAction.ShiftSignupConfirmed,
                    nameof(ShiftSignup), signup.Id,
                    $"Range signup for '{rota.Name}' day {shift.DayOffset} (block {blockId})",
                    userId);
            }
        }

        await _dbContext.SaveChangesAsync();

        if (autoConfirm)
        {
            await DispatchSignupChangeNotificationAsync(lastSignup!,
                $"Range signup for '{rota.Name}' ({shiftsInRange.Count} shifts, confirmed).");
        }

        return SignupResult.Ok(lastSignup!, warning);
    }

    public async Task<SignupResult> ApproveRangeAsync(Guid signupBlockId, Guid reviewerUserId)
    {
        var signups = await _dbContext.ShiftSignups
            .Include(s => s.Shift).ThenInclude(s => s.Rota).ThenInclude(r => r.EventSettings)
            .Include(s => s.Shift).ThenInclude(s => s.Rota).ThenInclude(r => r.Team)
            .Include(s => s.Shift).ThenInclude(s => s.ShiftSignups)
            .Where(s => s.SignupBlockId == signupBlockId && s.Status == SignupStatus.Pending)
            .ToListAsync();

        if (signups.Count == 0) return SignupResult.Fail("No pending signups found for this block.");

        var warnings = new List<string>();
        var now = _clock.GetCurrentInstant();

        foreach (var signup in signups)
        {
            var es = signup.Shift.Rota.EventSettings;

            // Capacity check (same as single-item ApproveAsync)
            var confirmedCount = signup.Shift.ShiftSignups.Count(d => d.Status == SignupStatus.Confirmed);
            if (confirmedCount >= signup.Shift.MaxVolunteers)
                warnings.Add($"Day {signup.Shift.DayOffset} is at capacity.");

            // EE cap revalidation for build shifts
            if (signup.Shift.IsEarlyEntry)
            {
                var eeWarning = await CheckEeCapAsync(es, signup.Shift.DayOffset);
                if (eeWarning is not null)
                    warnings.Add(eeWarning);
            }

            // EE freeze check
            if (signup.Shift.IsEarlyEntry && es.EarlyEntryClose.HasValue && now >= es.EarlyEntryClose.Value)
            {
                var isPrivileged = await IsPrivilegedAsync(reviewerUserId, signup.Shift.Rota.TeamId);
                if (!isPrivileged)
                    return SignupResult.Fail("Cannot approve build shift signups after early entry close.");
            }

            signup.Confirm(reviewerUserId, _clock);

            await _auditLogService.LogAsync(
                AuditAction.ShiftSignupConfirmed, nameof(ShiftSignup), signup.Id,
                $"Range approved for shift '{signup.Shift.Rota.Name}' day {signup.Shift.DayOffset} (block {signupBlockId})",
                reviewerUserId);
        }

        await _dbContext.SaveChangesAsync();

        await DispatchSignupChangeNotificationAsync(signups[0],
            $"Range approved ({signups.Count} shifts) for '{signups[0].Shift.Rota.Name}'.");

        var warning = warnings.Count > 0 ? string.Join(" ", warnings.Distinct(StringComparer.Ordinal)) : null;
        return SignupResult.Ok(signups[0], warning);
    }

    public async Task<SignupResult> RefuseRangeAsync(Guid signupBlockId, Guid reviewerUserId, string? reason)
    {
        var signups = await _dbContext.ShiftSignups
            .Include(s => s.Shift).ThenInclude(s => s.Rota)
            .Where(s => s.SignupBlockId == signupBlockId && s.Status == SignupStatus.Pending)
            .ToListAsync();

        if (signups.Count == 0) return SignupResult.Fail("No pending signups found for this block.");

        foreach (var signup in signups)
        {
            signup.Refuse(reviewerUserId, _clock, reason);

            await _auditLogService.LogAsync(
                AuditAction.ShiftSignupRefused, nameof(ShiftSignup), signup.Id,
                $"Range refused for shift '{signup.Shift.Rota.Name}' day {signup.Shift.DayOffset} (block {signupBlockId})" +
                (reason is not null ? $": {reason}" : ""),
                reviewerUserId);
        }

        await _dbContext.SaveChangesAsync();

        await DispatchSignupChangeNotificationAsync(signups[0],
            $"Range refused ({signups.Count} shifts) for '{signups[0].Shift.Rota.Name}'.");

        return SignupResult.Ok(signups[0]);
    }

    public async Task BailRangeAsync(Guid signupBlockId, Guid actorUserId, string? reason = null)
    {
        var signups = await _dbContext.ShiftSignups
            .Include(s => s.Shift).ThenInclude(s => s.Rota).ThenInclude(r => r.EventSettings)
            .Include(s => s.Shift).ThenInclude(s => s.Rota).ThenInclude(r => r.Team)
            .Include(s => s.Shift).ThenInclude(s => s.ShiftSignups)
            .Where(s => s.SignupBlockId == signupBlockId &&
                        (s.Status == SignupStatus.Confirmed || s.Status == SignupStatus.Pending))
            .ToListAsync();

        if (signups.Count == 0) return;

        var firstSignup = signups[0];
        var es = firstSignup.Shift.Rota.EventSettings;
        var now = _clock.GetCurrentInstant();
        var isOwner = firstSignup.UserId == actorUserId;
        var isPrivileged = await IsPrivilegedAsync(actorUserId, firstSignup.Shift.Rota.TeamId);

        if (!isOwner && !isPrivileged)
            throw new InvalidOperationException("Not authorized to bail this signup block.");

        // EE freeze check — if any shift is build-period and past EarlyEntryClose
        if (signups.Any(s => s.Shift.IsEarlyEntry) && es.EarlyEntryClose.HasValue && now >= es.EarlyEntryClose.Value && !isPrivileged)
            throw new InvalidOperationException("Cannot bail from build shifts after early entry close.");

        foreach (var signup in signups)
        {
            signup.Bail(actorUserId, _clock, reason);

            await _auditLogService.LogAsync(
                AuditAction.ShiftSignupBailed, nameof(ShiftSignup), signup.Id,
                $"Range bail from '{signup.Shift.Rota.Name}' day {signup.Shift.DayOffset} (block {signupBlockId})" +
                (reason is not null ? $": {reason}" : ""),
                actorUserId);
        }

        await _dbContext.SaveChangesAsync();

        await DispatchSignupChangeNotificationAsync(firstSignup,
            $"Range bail from '{firstSignup.Shift.Rota.Name}' ({signups.Count} shifts).");

        // Check coverage gaps for each bailed shift
        foreach (var signup in signups)
        {
            await CheckAndNotifyCoverageGapAsync(signup);
        }
    }

    public async Task<IReadOnlyList<ShiftSignup>> GetByUserAsync(Guid userId, Guid? eventSettingsId = null)
    {
        var query = _dbContext.ShiftSignups
            .Include(d => d.Shift).ThenInclude(s => s.Rota).ThenInclude(r => r.Team)
            .Include(d => d.Shift).ThenInclude(s => s.Rota).ThenInclude(r => r.EventSettings)
            .Where(d => d.UserId == userId);

        if (eventSettingsId.HasValue)
            query = query.Where(d => d.Shift.Rota.EventSettingsId == eventSettingsId.Value);

        return await query.OrderBy(d => d.Shift.DayOffset).ThenBy(d => d.Shift.StartTime).ToListAsync();
    }

    public async Task<ShiftSignup?> GetByIdAsync(Guid signupId)
    {
        return await _dbContext.ShiftSignups
            .Include(d => d.Shift).ThenInclude(s => s.Rota).ThenInclude(r => r.Team)
            .FirstOrDefaultAsync(d => d.Id == signupId);
    }

    public async Task<ShiftSignup?> GetByBlockIdFirstAsync(Guid signupBlockId)
    {
        return await _dbContext.ShiftSignups
            .Include(s => s.Shift).ThenInclude(s => s.Rota)
            .FirstOrDefaultAsync(s => s.SignupBlockId == signupBlockId);
    }

    public async Task<IReadOnlyList<ShiftSignup>> GetByShiftAsync(Guid shiftId)
    {
        return await _dbContext.ShiftSignups
            .Include(d => d.User)
            .Include(d => d.Shift)
                .ThenInclude(s => s.Rota)
            .Where(d => d.ShiftId == shiftId)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<ShiftSignup>> GetNoShowHistoryAsync(Guid userId)
    {
        return await _dbContext.ShiftSignups
            .AsNoTracking()
            .Include(s => s.Shift).ThenInclude(sh => sh.Rota).ThenInclude(r => r.Team)
            .Include(s => s.Shift).ThenInclude(sh => sh.Rota).ThenInclude(r => r.EventSettings)
            .Include(s => s.ReviewedByUser)
            .Where(s => s.UserId == userId && s.Status == SignupStatus.NoShow)
            .OrderByDescending(s => s.ReviewedAt)
            .ToListAsync();
    }

    public async Task<(HashSet<Guid> ShiftIds, Dictionary<Guid, SignupStatus> Statuses)> GetActiveSignupStatusesAsync(
        Guid userId, Guid eventSettingsId)
    {
        var signups = await GetByUserAsync(userId, eventSettingsId);
        return ShiftSignupHelper.ResolveActiveStatuses(signups);
    }

    private async Task<ShiftSignup?> GetSignupWithShiftAsync(Guid signupId)
    {
        return await _dbContext.ShiftSignups
            .Include(d => d.Shift).ThenInclude(s => s.Rota).ThenInclude(r => r.EventSettings)
            .Include(d => d.Shift).ThenInclude(s => s.Rota).ThenInclude(r => r.Team)
            .Include(d => d.Shift).ThenInclude(s => s.ShiftSignups)
            .FirstOrDefaultAsync(d => d.Id == signupId);
    }

    private async Task<string?> CheckOverlapAsync(Guid userId, Shift targetShift, EventSettings es)
    {
        var targetStart = targetShift.GetAbsoluteStart(es);
        var targetEnd = targetShift.GetAbsoluteEnd(es);

        var userSignups = await _dbContext.ShiftSignups
            .Include(d => d.Shift).ThenInclude(s => s.Rota).ThenInclude(r => r.EventSettings)
            .Include(d => d.Shift).ThenInclude(s => s.Rota).ThenInclude(r => r.Team)
            .Where(d => d.UserId == userId &&
                        d.ShiftId != targetShift.Id &&
                        d.Status == SignupStatus.Confirmed)
            .ToListAsync();

        foreach (var existing in userSignups)
        {
            var existingEs = existing.Shift.Rota.EventSettings;
            var existingStart = existing.Shift.GetAbsoluteStart(existingEs);
            var existingEnd = existing.Shift.GetAbsoluteEnd(existingEs);

            if (targetStart < existingEnd && targetEnd > existingStart)
            {
                var tz = DateTimeZoneProviders.Tzdb[existingEs.TimeZoneId];
                var dateStr = existingStart.InZone(tz).ToString("ddd MMM d HH:mm", null);
                var teamName = existing.Shift.Rota.Team?.Name ?? "Unknown";
                return $"Time conflict with '{existing.Shift.Rota.Name}' ({teamName}, {dateStr}).";
            }
        }

        return null;
    }

    private async Task<string?> CheckEeCapAsync(EventSettings es, int dayOffset)
    {
        var availableSlots = _shiftMgmt.GetAvailableEeSlots(es, dayOffset);
        if (availableSlots <= 0)
            return "Early entry capacity reached for this day.";

        var currentEeCount = await _dbContext.ShiftSignups
            .Where(d => d.Status == SignupStatus.Confirmed &&
                        d.Shift.Rota.EventSettingsId == es.Id &&
                        d.Shift.DayOffset == dayOffset)
            .Select(d => d.UserId)
            .Distinct()
            .CountAsync();

        if (currentEeCount >= availableSlots)
            return "Early entry capacity reached.";

        return null;
    }

    private async Task<bool> IsPrivilegedAsync(Guid userId, Guid departmentTeamId)
    {
        return await _shiftMgmt.CanApproveSignupsAsync(userId, departmentTeamId);
    }

    /// <summary>
    /// Checks if a bail created a coverage gap (confirmed count below MinVolunteers)
    /// and notifies team coordinators if so.
    /// </summary>
    private async Task CheckAndNotifyCoverageGapAsync(ShiftSignup signup)
    {
        try
        {
            var shift = signup.Shift;
            if (shift.MinVolunteers <= 0)
                return;

            var confirmedCount = shift.ShiftSignups.Count(s => s.Status == SignupStatus.Confirmed);
            if (confirmedCount >= shift.MinVolunteers)
                return;

            var teamId = shift.Rota.TeamId;
            var coordinatorIds = await _dbContext.TeamMembers
                .Where(tm => tm.TeamId == teamId
                    && tm.LeftAt == null
                    && tm.Role == TeamMemberRole.Coordinator)
                .Select(tm => tm.UserId)
                .ToListAsync();

            if (coordinatorIds.Count == 0)
                return;

            await _notificationService.SendAsync(
                NotificationSource.ShiftCoverageGap,
                NotificationClass.Actionable,
                NotificationPriority.High,
                $"Coverage gap: {shift.Rota.Name} day {shift.DayOffset}",
                coordinatorIds,
                body: $"Only {confirmedCount}/{shift.MinVolunteers} volunteers confirmed.",
                actionUrl: $"/Shifts/Dashboard?departmentId={teamId}",
                actionLabel: "Find cover \u2192");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch ShiftCoverageGap notification for signup {SignupId}", signup.Id);
        }
    }

    /// <summary>
    /// Dispatches a ShiftSignupChange notification to the team coordinators of the shift's department.
    /// Fire-and-forget style — failures are logged but do not affect the signup operation.
    /// </summary>
    private async Task DispatchSignupChangeNotificationAsync(ShiftSignup signup, string changeDescription)
    {
        try
        {
            var teamId = signup.Shift.Rota.TeamId;
            var rotaName = signup.Shift.Rota.Name;

            // Find coordinators for this department team
            var coordinatorIds = await _dbContext.TeamMembers
                .Where(tm => tm.TeamId == teamId
                    && tm.LeftAt == null
                    && tm.Role == TeamMemberRole.Coordinator)
                .Select(tm => tm.UserId)
                .ToListAsync();

            if (coordinatorIds.Count == 0)
                return;

            await _notificationService.SendAsync(
                NotificationSource.ShiftSignupChange,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                $"Shift signup change: {rotaName}",
                coordinatorIds,
                body: changeDescription,
                actionUrl: $"/Shifts/Dashboard?departmentId={teamId}",
                actionLabel: "View \u2192");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch ShiftSignupChange notification for signup {SignupId}", signup.Id);
        }
    }
}
