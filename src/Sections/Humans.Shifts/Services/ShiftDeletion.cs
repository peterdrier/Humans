using Humans.AuditLog.Contracts;
using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Humans.Shifts.Contracts;
using Humans.Shifts.Data;
using Humans.Shifts.Domain;

namespace Humans.Shifts.Services;

internal sealed class ShiftDeletion(
    IShiftManagementRepository repo,
    IAuditLogService auditLogService,
    CachingShiftViewService viewInvalidator,
    ILogger<ShiftManagementService> logger) : IApplicationService
{
    internal async Task<ShiftDeletionResult> DeleteRotaAsync(Guid rotaId, Guid actorUserId)
    {
        var rota = await repo.GetRotaAsync(rotaId, RotaReadShape.ShiftsWithSignups);
        if (rota is null) return RefuseDeletion(rotaId, "Shifts_Signup_RotaNotFound");

        var confirmedCount = rota.Shifts
            .SelectMany(s => s.ShiftSignups)
            .Count(d => d.Status == SignupStatus.Confirmed);

        if (confirmedCount > 0)
            return RefuseDeletion(rotaId, "Shifts_Delete_ConfirmedSignups", confirmedCount);

        // Captured before the cascade, for the audit after it commits.
        var removedSignups = rota.Shifts.SelectMany(s => s.ShiftSignups).ToList();
        var affectedUserIds = removedSignups.Select(s => s.UserId).Distinct().ToList();

        try
        {
            await repo.DeleteRotaCascadeAsync(rotaId);
        }
        finally
        {
            viewInvalidator.InvalidateRota(rotaId);
            foreach (var uid in affectedUserIds)
                viewInvalidator.InvalidateUser(uid);
        }
        await AuditDeletedPendingSignupsAsync(removedSignups, actorUserId, $"rota '{rota.Name}' was deleted");
        return new ShiftDeletionResult();
    }

    internal async Task<ShiftDeletionResult> DeleteShiftAsync(Guid shiftId, Guid actorUserId)
    {
        var shift = await repo.GetShiftAsync(shiftId, ShiftReadShape.Rota | ShiftReadShape.ShiftSignups);
        if (shift is null) return RefuseDeletion(shiftId, "Shifts_Signup_ShiftNotFound");

        var confirmedCount = shift.ShiftSignups.Count(d => d.Status == SignupStatus.Confirmed);
        if (confirmedCount > 0)
            return RefuseDeletion(shiftId, "Shifts_Delete_ConfirmedSignups", confirmedCount);

        var rotaId = shift.RotaId;
        var removedSignups = shift.ShiftSignups.ToList();
        var affectedUserIds = removedSignups.Select(s => s.UserId).Distinct().ToList();

        try
        {
            await repo.DeleteShiftCascadeAsync(shiftId);
        }
        finally
        {
            viewInvalidator.InvalidateShift(shiftId);
            viewInvalidator.InvalidateRota(rotaId);
            foreach (var uid in affectedUserIds)
                viewInvalidator.InvalidateUser(uid);
        }
        await AuditDeletedPendingSignupsAsync(removedSignups, actorUserId,
            $"shift in '{shift.Rota.Name}' on day {shift.DayOffset} was deleted");
        return new ShiftDeletionResult();
    }

    private async Task AuditDeletedPendingSignupsAsync(
        IReadOnlyList<ShiftSignup> signups, Guid actorUserId, string reason)
    {
        foreach (var signup in signups.Where(s => s.Status == SignupStatus.Pending))
        {
            await auditLogService.LogAsync(ShiftsAuditActions.ShiftSignupCancelled, nameof(ShiftSignup), signup.Id,
                $"shift {signup.ShiftId}: {reason}", actorUserId,
                relatedEntityId: signup.UserId, relatedEntityType: AuditSubjectTypes.User);
        }
    }

    private ShiftDeletionResult RefuseDeletion(Guid entityId, string errorKey, int confirmedSignups = 0)
    {
        logger.LogWarning("Shift/rota deletion rejected for {EntityId}: {ErrorKey}, confirmed signups {ConfirmedSignups}",
            entityId, errorKey, confirmedSignups);
        return new ShiftDeletionResult(errorKey, confirmedSignups);
    }

}
