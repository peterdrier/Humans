using Humans.Application.Interfaces;
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
    private readonly IClock _clock;
    private readonly ILogger<ShiftSignupService> _logger;

    public ShiftSignupService(
        HumansDbContext dbContext,
        IShiftManagementService shiftMgmt,
        IAuditLogService auditLogService,
        IClock clock,
        ILogger<ShiftSignupService> logger)
    {
        _dbContext = dbContext;
        _shiftMgmt = shiftMgmt;
        _auditLogService = auditLogService;
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
                userId, "Self");
        }

        await _dbContext.SaveChangesAsync();

        return SignupResult.Ok(signup, warning);
    }

    public async Task<SignupResult> ApproveAsync(Guid signupId, Guid reviewerUserId)
    {
        var signup = await LoadSignupWithShiftAsync(signupId);
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
            reviewerUserId, "Reviewer");

        await _dbContext.SaveChangesAsync();

        return SignupResult.Ok(signup, warning);
    }

    public async Task<SignupResult> RefuseAsync(Guid signupId, Guid reviewerUserId, string? reason)
    {
        var signup = await LoadSignupWithShiftAsync(signupId);
        if (signup is null) return SignupResult.Fail("Signup not found.");

        signup.Refuse(reviewerUserId, _clock, reason);

        await _auditLogService.LogAsync(
            AuditAction.ShiftSignupRefused, nameof(ShiftSignup), signup.Id,
            $"Refused signup for shift '{signup.Shift.Rota.Name}'" + (reason is not null ? $": {reason}" : ""),
            reviewerUserId, "Reviewer");

        await _dbContext.SaveChangesAsync();

        return SignupResult.Ok(signup);
    }

    public async Task<SignupResult> BailAsync(Guid signupId, Guid actorUserId, string? reason)
    {
        var signup = await LoadSignupWithShiftAsync(signupId);
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
            actorUserId, "Actor");

        await _dbContext.SaveChangesAsync();

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
            enrollerUserId, "Enroller",
            userId, nameof(User));

        await _dbContext.SaveChangesAsync();

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
                enrollerUserId, "Enroller",
                userId, nameof(User));
        }

        await _dbContext.SaveChangesAsync();

        return SignupResult.Ok(firstSignup!, warning);
    }

    public async Task<SignupResult> MarkNoShowAsync(Guid signupId, Guid reviewerUserId)
    {
        var signup = await LoadSignupWithShiftAsync(signupId);
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
            reviewerUserId, "Reviewer");

        await _dbContext.SaveChangesAsync();

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
            var eeWarning = await CheckEeCapAsync(es, shiftsInRange[0].DayOffset);
            if (eeWarning is not null)
                warning = warning is null ? eeWarning : $"{warning} {eeWarning}";
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
                    userId, "Self");
            }
        }

        await _dbContext.SaveChangesAsync();

        return SignupResult.Ok(lastSignup!, warning);
    }

    public async Task BailRangeAsync(Guid signupBlockId, Guid actorUserId, string? reason = null)
    {
        var signups = await _dbContext.ShiftSignups
            .Include(s => s.Shift).ThenInclude(s => s.Rota).ThenInclude(r => r.EventSettings)
            .Include(s => s.Shift).ThenInclude(s => s.Rota).ThenInclude(r => r.Team)
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
                actorUserId, "Actor");
        }

        await _dbContext.SaveChangesAsync();
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

    private async Task<ShiftSignup?> LoadSignupWithShiftAsync(Guid signupId)
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
                        d.Shift.DayOffset < 0)
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
}
