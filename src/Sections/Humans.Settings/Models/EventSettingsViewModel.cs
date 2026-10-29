using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using Humans.Settings.Contracts;
using NodaTime;
using NodaTime.Text;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Humans.Settings.Models;

internal sealed class EventSettingsViewModel : IValidatableObject
{
    [BindRequired]
    public Guid? Id { get; set; }

    [Required, MaxLength(256)]
    public string EventName { get; set; } = string.Empty;

    [Required]
    public string GateOpeningDate { get; set; } = string.Empty;

    [BindRequired]
    public int BuildStartOffset { get; set; } = -25;
    [Range(0, int.MaxValue, ErrorMessage = "Event end must be on or after gate-opening day.")]
    [BindRequired]
    public int EventEndOffset { get; set; } = 6;
    [BindRequired]
    public int StrikeEndOffset { get; set; } = 9;





    [BindRequired]
    public string EarlyEntryCapacityJson { get; set; } = "{}";
    [BindRequired]
    public string? BarriosEarlyEntryAllocationJson { get; set; }

    [BindRequired]
    public string? EarlyEntryClose { get; set; }

    [BindRequired]
    public int? EarlyEntryStartOffset { get; set; }

    [BindRequired]
    public bool IsOffSeason { get; set; }

    /// <summary>Maps to Active/Inactive only; this form never withdraws a cycle.</summary>
    [BindRequired]
    public bool IsActive { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (StrikeEndOffset < EventEndOffset)
        {
            yield return new ValidationResult(
                "Strike end must be on or after event end.",
                [nameof(StrikeEndOffset), nameof(EventEndOffset)]);
        }

        if (EarlyEntryStartOffset is { } eeStartOffset
            && (eeStartOffset < BuildStartOffset || eeStartOffset >= 0))
        {
            yield return new ValidationResult(
                "Early entry start offset must be between build start and 0 (exclusive).",
                [nameof(EarlyEntryStartOffset)]);
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

/// <summary>The controller never touches the EF entity: the DTO is the section's own write surface too.</summary>
internal static class EventSettingsFormMapper
{
    private static readonly Domain.EventSettings SubPeriodDefaults = new();

    internal static EventSettingsViewModel ToViewModel(EventSettingsInfo src) => new()
    {
        Id = src.Id,
        EventName = src.EventName,
        GateOpeningDate = LocalDatePattern.Iso.Format(src.GateOpeningDate),
        BuildStartOffset = src.BuildStartOffset,
        EventEndOffset = src.EventEndOffset,
        StrikeEndOffset = src.StrikeEndOffset,
        EarlyEntryCapacityJson = JsonSerializer.Serialize(src.EarlyEntryCapacity),
        BarriosEarlyEntryAllocationJson = src.BarriosEarlyEntryAllocation is not null
            ? JsonSerializer.Serialize(src.BarriosEarlyEntryAllocation)
            : null,
        EarlyEntryClose = src.EarlyEntryClose.HasValue
            ? InstantPattern.General.Format(src.EarlyEntryClose.Value)
            : null,
        IsActive = src.Status == EventSettingsStatus.Active,
        EarlyEntryStartOffset = src.EarlyEntryStartOffset,
        IsOffSeason = src.IsOffSeason,
    };

    /// <summary>
    /// A blank form for a new event, shaped like <paramref name="previous"/> one year on: offsets and early-entry
    /// capacity carry over, the gate date and the name's year move forward, and the instant-valued close is cleared.
    /// </summary>
    internal static EventSettingsViewModel NextEvent(EventSettingsInfo? previous)
    {
        if (previous is null)
            return new EventSettingsViewModel();

        var next = ToViewModel(previous);
        var nextYear = previous.GateOpeningDate.PlusYears(1);
        next.Id = null;
        next.EventName = previous.EventName.Replace(
            previous.Year.ToString(CultureInfo.InvariantCulture),
            nextYear.Year.ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
        next.GateOpeningDate = LocalDatePattern.Iso.Format(nextYear);
        next.EarlyEntryClose = null;
        next.IsActive = false;
        next.IsOffSeason = false;
        return next;
    }

    internal static EventSettingsParseResult Parse(EventSettingsViewModel model)
    {
        var errors = new List<EventSettingsFormError>();

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

        // A new cycle mints its own id: Settings owns event ids.
        return new EventSettingsParseResult(
            new EventSettingsInfo(
                Id: model.Id ?? Guid.NewGuid(),
                EventName: model.EventName,
                Year: gateOpening.Year,
                // The global timezone; the service stores it on save.
                TimeZoneId: string.Empty,
                GateOpeningDate: gateOpening,
                BuildStartOffset: model.BuildStartOffset,
                EventEndOffset: model.EventEndOffset,
                StrikeEndOffset: model.StrikeEndOffset,
                // Shifts owns the build sub-periods now; a new row gets Settings' defaults as Shifts' fallback,
                // and an update never overwrites the stored ones.
                FirstCrewStartOffset: SubPeriodDefaults.FirstCrewStartOffset,
                SetupWeekStartOffset: SubPeriodDefaults.SetupWeekStartOffset,
                PreEventWeekStartOffset: SubPeriodDefaults.PreEventWeekStartOffset,
                FinishingWeekendStartOffset: SubPeriodDefaults.FinishingWeekendStartOffset,
                EarlyEntryCapacity: earlyEntryCapacity,
                BarriosEarlyEntryAllocation: barriosAllocation,
                EarlyEntryClose: earlyEntryClose,
                Status: model.IsActive
                    ? EventSettingsStatus.Active
                    : EventSettingsStatus.Inactive,
                EarlyEntryStartOffset: model.EarlyEntryStartOffset,
                IsOffSeason: model.IsOffSeason),
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
