namespace Humans.Shifts.Services.Dtos;

/// <summary>
/// Shifts' own per-event knobs (nobodies-collective/Humans#1631) — a DTO rather than the
/// <c>EventSettings</c> entity itself, so <see cref="Services.IShiftManagementService.GetKnobsAsync"/>
/// stays off the entity-read ratchet (docs/architecture/service-entity-boundary-ratchet.md).
/// </summary>
internal sealed record ShiftEventKnobs(bool IsShiftBrowsingOpen, int? GlobalVolunteerCap, int ReminderLeadTimeHours);
