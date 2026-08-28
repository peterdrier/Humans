using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Humans.Settings.Contracts;
using NodaTime;
using NodaTime.Text;

namespace Humans.Settings.Models;

/// <summary>
/// The app-wide event settings form. Only the values Settings owns after #1104 —
/// the Shifts knobs (browsing switch, volunteer cap, reminder lead time) stay on
/// the Shifts screen.
/// </summary>
internal sealed class EventSettingsViewModel : IValidatableObject
{
    public Guid? Id { get; set; }

    [Required, MaxLength(256)]
    public string EventName { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string TimeZoneId { get; set; } = "Europe/Madrid";

    [Required]
    public string GateOpeningDate { get; set; } = string.Empty;

    // The build window is [BuildStartOffset, 0) and the four sub-periods below
    // partition it, so build starts on the first-crew day.
    public int BuildStartOffset { get; set; } = -25;
    public int EventEndOffset { get; set; } = 6;
    public int StrikeEndOffset { get; set; } = 9;

    // Build sub-period boundaries — defaults match the entity defaults set by EF config.
    [Range(int.MinValue, -1, ErrorMessage = "First crew start must be a negative offset relative to gate-opening day.")]
    public int FirstCrewStartOffset { get; set; } = -25;

    [Range(int.MinValue, -1, ErrorMessage = "Set-up week start must be a negative offset relative to gate-opening day.")]
    public int SetupWeekStartOffset { get; set; } = -16;

    [Range(int.MinValue, -1, ErrorMessage = "Pre-event week start must be a negative offset relative to gate-opening day.")]
    public int PreEventWeekStartOffset { get; set; } = -9;

    [Range(int.MinValue, -1, ErrorMessage = "Finishing weekend start must be a negative offset relative to gate-opening day.")]
    public int FinishingWeekendStartOffset { get; set; } = -4;

    public string EarlyEntryCapacityJson { get; set; } = "{}";
    public string? BarriosEarlyEntryAllocationJson { get; set; }

    public string? EarlyEntryClose { get; set; }

    /// <summary>
    /// The form's view of <see cref="EventSettingsStatus"/>: the checkbox maps to
    /// Active/Inactive and never reaches <see cref="EventSettingsStatus.Deleted"/> —
    /// withdrawing a cycle is not something this form does.
    /// </summary>
    public bool IsActive { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // The build window opens on the first-crew day at the latest — a first-crew
        // boundary before it would name days no build shift can occupy.
        if (BuildStartOffset > FirstCrewStartOffset)
        {
            yield return new ValidationResult(
                $"Build starts at {BuildStartOffset} but first crew starts at {FirstCrewStartOffset}. "
                + "The build window is split into the four sub-periods below, so build start must be "
                + "the first crew day or earlier (more negative).",
                [nameof(BuildStartOffset), nameof(FirstCrewStartOffset)]);
        }

        if (FirstCrewStartOffset >= SetupWeekStartOffset
            || SetupWeekStartOffset >= PreEventWeekStartOffset
            || PreEventWeekStartOffset >= FinishingWeekendStartOffset)
        {
            yield return new ValidationResult(
                "Build sub-period offsets must be strictly ascending: First crew < Set-up week < Pre-event week < Finishing weekend.",
                [
                    nameof(FirstCrewStartOffset),
                    nameof(SetupWeekStartOffset),
                    nameof(PreEventWeekStartOffset),
                    nameof(FinishingWeekendStartOffset)
                ]);
        }
    }
}

internal sealed record EventSettingsFormError(string FieldName, string Message);

internal sealed record EventSettingsParseResult(
    EventSettingsInfo? Settings,
    IReadOnlyList<EventSettingsFormError> Errors)
{
    public bool Success => Settings is not null && Errors.Count == 0;
}

/// <summary>
/// Form ↔ <see cref="EventSettingsInfo"/>. The controller never touches the EF
/// entity — the DTO is the section's own write surface too.
/// </summary>
internal static class EventSettingsFormMapper
{
    internal static EventSettingsViewModel ToViewModel(EventSettingsInfo src) => new()
    {
        Id = src.Id,
        EventName = src.EventName,
        TimeZoneId = src.TimeZoneId,
        GateOpeningDate = LocalDatePattern.Iso.Format(src.GateOpeningDate),
        BuildStartOffset = src.BuildStartOffset,
        EventEndOffset = src.EventEndOffset,
        StrikeEndOffset = src.StrikeEndOffset,
        FirstCrewStartOffset = src.FirstCrewStartOffset,
        SetupWeekStartOffset = src.SetupWeekStartOffset,
        PreEventWeekStartOffset = src.PreEventWeekStartOffset,
        FinishingWeekendStartOffset = src.FinishingWeekendStartOffset,
        EarlyEntryCapacityJson = JsonSerializer.Serialize(src.EarlyEntryCapacity),
        BarriosEarlyEntryAllocationJson = src.BarriosEarlyEntryAllocation is not null
            ? JsonSerializer.Serialize(src.BarriosEarlyEntryAllocation)
            : null,
        EarlyEntryClose = src.EarlyEntryClose.HasValue
            ? InstantPattern.General.Format(src.EarlyEntryClose.Value)
            : null,
        IsActive = src.Status == EventSettingsStatus.Active,
    };

    internal static EventSettingsParseResult Parse(EventSettingsViewModel model)
    {
        var errors = new List<EventSettingsFormError>();

        // The form edits an existing row and never invents an id: Shifts' event_settings
        // still owns them, and a Settings-only id is an event that can hold no rota.
        if (model.Id is null)
        {
            errors.Add(new EventSettingsFormError(
                nameof(model.Id),
                "This form edits an existing event row. New rows arrive through /Settings/Admin/Carry."));
        }

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

        var earlyEntryCapacity = ParseDictionary(
            model.EarlyEntryCapacityJson, nameof(model.EarlyEntryCapacityJson), errors) ?? [];

        var barriosAllocation = string.IsNullOrWhiteSpace(model.BarriosEarlyEntryAllocationJson)
            ? null
            : ParseDictionary(
                model.BarriosEarlyEntryAllocationJson, nameof(model.BarriosEarlyEntryAllocationJson), errors);

        if (errors.Count > 0)
            return new EventSettingsParseResult(null, errors);

        var gateOpening = parsedDate.Value;

        return new EventSettingsParseResult(
            new EventSettingsInfo(
                Id: model.Id!.Value,
                EventName: model.EventName,
                // Year is the gate-opening year, never edited on its own.
                Year: gateOpening.Year,
                TimeZoneId: model.TimeZoneId,
                GateOpeningDate: gateOpening,
                BuildStartOffset: model.BuildStartOffset,
                EventEndOffset: model.EventEndOffset,
                StrikeEndOffset: model.StrikeEndOffset,
                FirstCrewStartOffset: model.FirstCrewStartOffset,
                SetupWeekStartOffset: model.SetupWeekStartOffset,
                PreEventWeekStartOffset: model.PreEventWeekStartOffset,
                FinishingWeekendStartOffset: model.FinishingWeekendStartOffset,
                EarlyEntryCapacity: earlyEntryCapacity,
                BarriosEarlyEntryAllocation: barriosAllocation,
                EarlyEntryClose: earlyEntryClose,
                Status: model.IsActive
                    ? EventSettingsStatus.Active
                    : EventSettingsStatus.Inactive),
            []);
    }

    private static Dictionary<int, int>? ParseDictionary(
        string? json, string fieldName, List<EventSettingsFormError> errors)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<int, int>>(json);
        }
        catch (JsonException ex)
        {
            errors.Add(new EventSettingsFormError(fieldName, $"Invalid JSON: {ex.Message}"));
            return null;
        }
    }
}
