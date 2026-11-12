using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Shifts.Contracts;
using Humans.Shifts.Data;
using Humans.Shifts.Domain;
using Humans.Shifts.Services.Dtos;
using NodaTime;

namespace Humans.Shifts.Services;

internal sealed class ShiftEventSettings(
    IShiftManagementRepository repo,
    IAuditLogService auditLogService,
    IClock clock) : IApplicationService
{
    internal async Task<ShiftEventKnobs?> GetKnobsAsync(Guid eventSettingsId, CancellationToken ct = default)
    {
        var entity = await repo.GetEventSettingsByIdAsync(eventSettingsId, ct);
        return entity is null
            ? null
            : new ShiftEventKnobs(entity.IsShiftBrowsingOpen);
    }

    internal async Task SaveKnobsAsync(
        Guid eventSettingsId, bool isShiftBrowsingOpen, BuildSubPeriodStarts subPeriods, Guid actorUserId)
    {
        var entity = await EnsureKnobsRowAsync(eventSettingsId);
        entity.IsShiftBrowsingOpen = isShiftBrowsingOpen;
        SetSubPeriods(entity, subPeriods);
        entity.UpdatedAt = clock.GetCurrentInstant();
        await repo.SaveEventSettingsAsync(entity, EntityMutationMode.Update);
        await auditLogService.LogAsync(
            ShiftsAuditActions.ShiftSettingsUpdated, nameof(EventSettings), eventSettingsId,
            $"Shift settings saved: browsing {(isShiftBrowsingOpen ? "open" : "closed")}, "
            + $"build sub-periods start first crew {subPeriods.FirstCrew}, set-up week {subPeriods.SetupWeek}, "
            + $"pre-event week {subPeriods.PreEventWeek}, finishing weekend {subPeriods.FinishingWeekend}.",
            actorUserId);
    }

    private static void SetSubPeriods(EventSettings entity, BuildSubPeriodStarts starts)
    {
        entity.SubPeriodFirstCrewStart = starts.FirstCrew;
        entity.SubPeriodSetupWeekStart = starts.SetupWeek;
        entity.SubPeriodPreEventWeekStart = starts.PreEventWeek;
        entity.SubPeriodFinishingWeekendStart = starts.FinishingWeekend;
    }

    /// <summary>
    /// Creates the knobs row on demand: "active event" is Settings' concept, and a Shifts row exists only once a
    /// rota or knob edit needs it. Callers that mutate the row save it themselves.
    /// </summary>
    internal async Task<EventSettings> EnsureKnobsRowAsync(Guid eventSettingsId)
    {
        var existing = await repo.GetEventSettingsByIdAsync(eventSettingsId);
        if (existing is not null) return existing;

        var now = clock.GetCurrentInstant();
        var created = new EventSettings { Id = eventSettingsId, CreatedAt = now, UpdatedAt = now };
        SetSubPeriods(created, BuildSubPeriodStarts.Default);
        await repo.SaveEventSettingsAsync(created, EntityMutationMode.Add);
        return created;
    }

    internal async Task SetShiftBrowsingOpenAsync(Guid eventSettingsId, bool isOpen)
    {
        var entity = await EnsureKnobsRowAsync(eventSettingsId);
        entity.IsShiftBrowsingOpen = isOpen;
        entity.UpdatedAt = clock.GetCurrentInstant();
        await repo.SaveEventSettingsAsync(entity, EntityMutationMode.Update);
    }

}
