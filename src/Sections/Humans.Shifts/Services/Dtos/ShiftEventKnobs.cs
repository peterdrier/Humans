namespace Humans.Shifts.Services.Dtos;

/// <summary>
/// A DTO, not the entity, to keep <see cref="Services.IShiftManagement.GetKnobsAsync"/> off the entity-read ratchet.
/// </summary>
internal sealed record ShiftEventKnobs(bool IsShiftBrowsingOpen, int? GlobalVolunteerCap, int ReminderLeadTimeHours);
