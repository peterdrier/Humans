using System.Text.Json;
using Humans.Domain.Entities;
using NodaTime;
using NodaTime.Text;

namespace Humans.Web.Models.Shifts;

public sealed record EventSettingsFormError(string FieldName, string Message);

public sealed record EventSettingsFormParseResult(
    EventSettingsDraft? Draft,
    IReadOnlyList<EventSettingsFormError> Errors)
{
    public bool Success => Draft is not null && Errors.Count == 0;
}

public sealed record EventSettingsDraft(
    Guid? Id,
    string EventName,
    string TimeZoneId,
    LocalDate GateOpeningDate,
    int BuildStartOffset,
    int EventEndOffset,
    int StrikeEndOffset,
    int FirstCrewStartOffset,
    int SetupWeekStartOffset,
    int PreEventWeekStartOffset,
    int FinishingWeekendStartOffset,
    Dictionary<int, int> EarlyEntryCapacity,
    Dictionary<int, int>? BarriosEarlyEntryAllocation,
    Instant? EarlyEntryClose,
    bool IsShiftBrowsingOpen,
    int? GlobalVolunteerCap,
    int ReminderLeadTimeHours,
    bool IsActive);

public static class EventSettingsFormMapper
{
    public static EventSettingsFormParseResult Parse(EventSettingsViewModel model)
    {
        var errors = new List<EventSettingsFormError>();

        if (DateTimeZoneProviders.Tzdb.GetZoneOrNull(model.TimeZoneId) is null)
            errors.Add(new EventSettingsFormError(nameof(model.TimeZoneId), "Invalid IANA timezone ID."));

        var parsedDate = LocalDatePattern.Iso.Parse(model.GateOpeningDate);
        if (!parsedDate.Success)
            errors.Add(new EventSettingsFormError(nameof(model.GateOpeningDate), "Invalid date format."));

        Instant? earlyEntryClose = null;
        if (!string.IsNullOrEmpty(model.EarlyEntryClose))
        {
            var parsedInstant = InstantPattern.General.Parse(model.EarlyEntryClose);
            if (parsedInstant.Success)
                earlyEntryClose = parsedInstant.Value;
            else
                errors.Add(new EventSettingsFormError(nameof(model.EarlyEntryClose), "Invalid UTC instant format."));
        }

        if (errors.Count > 0)
            return new EventSettingsFormParseResult(null, errors);

        var earlyEntryCapacity = !string.IsNullOrEmpty(model.EarlyEntryCapacityJson)
            ? JsonSerializer.Deserialize<Dictionary<int, int>>(model.EarlyEntryCapacityJson) ?? new()
            : new Dictionary<int, int>();

        var barriosAllocation = !string.IsNullOrEmpty(model.BarriosEarlyEntryAllocationJson)
            ? JsonSerializer.Deserialize<Dictionary<int, int>>(model.BarriosEarlyEntryAllocationJson)
            : null;

        var draft = new EventSettingsDraft(
            model.Id,
            model.EventName,
            model.TimeZoneId,
            parsedDate.Value,
            model.BuildStartOffset,
            model.EventEndOffset,
            model.StrikeEndOffset,
            model.FirstCrewStartOffset,
            model.SetupWeekStartOffset,
            model.PreEventWeekStartOffset,
            model.FinishingWeekendStartOffset,
            earlyEntryCapacity,
            barriosAllocation,
            earlyEntryClose,
            model.IsShiftBrowsingOpen,
            model.GlobalVolunteerCap,
            model.ReminderLeadTimeHours,
            model.IsActive);

        return new EventSettingsFormParseResult(draft, []);
    }

    public static EventSettings Create(EventSettingsDraft draft, Instant now)
    {
        var entity = new EventSettings
        {
            Id = Guid.NewGuid(),
            CreatedAt = now
        };
        Apply(entity, draft);
        return entity;
    }

    public static void Apply(EventSettings entity, EventSettingsDraft draft)
    {
        entity.EventName = draft.EventName;
        entity.TimeZoneId = draft.TimeZoneId;
        entity.GateOpeningDate = draft.GateOpeningDate;
        entity.Year = draft.GateOpeningDate.Year;
        entity.BuildStartOffset = draft.BuildStartOffset;
        entity.EventEndOffset = draft.EventEndOffset;
        entity.StrikeEndOffset = draft.StrikeEndOffset;
        entity.FirstCrewStartOffset = draft.FirstCrewStartOffset;
        entity.SetupWeekStartOffset = draft.SetupWeekStartOffset;
        entity.PreEventWeekStartOffset = draft.PreEventWeekStartOffset;
        entity.FinishingWeekendStartOffset = draft.FinishingWeekendStartOffset;
        entity.EarlyEntryCapacity = draft.EarlyEntryCapacity;
        entity.BarriosEarlyEntryAllocation = draft.BarriosEarlyEntryAllocation;
        entity.EarlyEntryClose = draft.EarlyEntryClose;
        entity.IsShiftBrowsingOpen = draft.IsShiftBrowsingOpen;
        entity.GlobalVolunteerCap = draft.GlobalVolunteerCap;
        entity.ReminderLeadTimeHours = draft.ReminderLeadTimeHours;
        entity.IsActive = draft.IsActive;
    }
}
