using System.Text.Json;
using Humans.Shifts.Contracts;
using Humans.Shifts.Domain;
using NodaTime;
using NodaTime.Text;

namespace Humans.Shifts.Models;

internal sealed record EventSettingsFormError(string FieldName, string Message);

internal sealed record EventSettingsFormParseResult(
    EventSettingsDraft? Draft,
    IReadOnlyList<EventSettingsFormError> Errors)
{
    public bool Success => Draft is not null && Errors.Count == 0;
}

internal sealed record EventSettingsDraft(
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

internal static class EventSettingsFormMapper
{
    /// <summary>
    /// Maps a Shifts-owned <see cref="EventSettings"/> row plus its Settings-sourced
    /// calendar to the admin form's view model. The calendar (app-wide fields) is
    /// read-only display sourced from Settings; falling back to the row's own columns
    /// only covers a brand-new row Settings hasn't carried yet (nobodies-collective/Humans#1630).
    /// </summary>
    internal static EventSettingsViewModel ToViewModel(EventSettings es, BurnSettingsInfo? calendar) => new()
    {
        Id = es.Id,
        EventName = calendar?.EventName ?? es.EventName,
        TimeZoneId = calendar?.TimeZoneId ?? es.TimeZoneId,
        GateOpeningDate = LocalDatePattern.Iso.Format(calendar?.GateOpeningDate ?? es.GateOpeningDate),
        BuildStartOffset = calendar?.BuildStartOffset ?? es.BuildStartOffset,
        EventEndOffset = calendar?.EventEndOffset ?? es.EventEndOffset,
        StrikeEndOffset = calendar?.StrikeEndOffset ?? es.StrikeEndOffset,
        FirstCrewStartOffset = calendar?.FirstCrewStartOffset ?? es.FirstCrewStartOffset,
        SetupWeekStartOffset = calendar?.SetupWeekStartOffset ?? es.SetupWeekStartOffset,
        PreEventWeekStartOffset = calendar?.PreEventWeekStartOffset ?? es.PreEventWeekStartOffset,
        FinishingWeekendStartOffset = calendar?.FinishingWeekendStartOffset ?? es.FinishingWeekendStartOffset,
        EarlyEntryCapacityJson = JsonSerializer.Serialize(calendar?.EarlyEntryCapacity ?? es.EarlyEntryCapacity),
        BarriosEarlyEntryAllocationJson = (calendar?.BarriosEarlyEntryAllocation ?? es.BarriosEarlyEntryAllocation) is { } barrios
            ? JsonSerializer.Serialize(barrios)
            : null,
        EarlyEntryClose = (calendar?.EarlyEntryClose ?? es.EarlyEntryClose) is { } earlyEntryClose
            ? InstantPattern.General.Format(earlyEntryClose)
            : null,
        IsShiftBrowsingOpen = es.IsShiftBrowsingOpen,
        GlobalVolunteerCap = es.GlobalVolunteerCap,
        ReminderLeadTimeHours = es.ReminderLeadTimeHours,
        IsActive = es.IsActive,
    };

    internal static EventSettingsFormParseResult Parse(EventSettingsViewModel model)
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

        var earlyEntryCapacity = ParseOffsetMap(
            model.EarlyEntryCapacityJson, nameof(model.EarlyEntryCapacityJson), errors) ?? new();
        var barriosAllocation = ParseOffsetMap(
            model.BarriosEarlyEntryAllocationJson, nameof(model.BarriosEarlyEntryAllocationJson), errors);

        if (errors.Count > 0)
            return new EventSettingsFormParseResult(null, errors);

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

    /// <summary>
    /// Parses a day-offset → count JSON object from the form. Empty input is
    /// <c>null</c>; malformed input is a field error, not an exception.
    /// </summary>
    private static Dictionary<int, int>? ParseOffsetMap(
        string? json, string fieldName, List<EventSettingsFormError> errors)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<int, int>>(json);
        }
        catch (JsonException)
        {
            // Not logged: the admin typed the JSON into the form, and the field error hands it back to them.
            errors.Add(new EventSettingsFormError(fieldName, "Invalid JSON: expected {\"dayOffset\": count, ...}."));
            return null;
        }
    }

    internal static EventSettings Create(EventSettingsDraft draft, Instant now)
    {
        var entity = new EventSettings
        {
            Id = Guid.NewGuid(),
            CreatedAt = now
        };
        Apply(entity, draft);
        return entity;
    }

    internal static void Apply(EventSettings entity, EventSettingsDraft draft)
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
