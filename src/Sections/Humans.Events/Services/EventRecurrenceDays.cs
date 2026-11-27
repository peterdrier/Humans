using System.Globalization;
using NodaTime;

namespace Humans.Events.Services;

/// <summary>
/// Stored form: comma-separated day offsets from the gate date (<c>"0,2,4"</c>). The bulk-upload CSV uses day names
/// (<c>"Mon Wed Fri"</c>).
/// </summary>
internal static class EventRecurrenceDays
{
    private static readonly string[] DayNames = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    /// <summary>Preserves the empty-selection fallback.</summary>
    internal static bool HasOnlyEventOffsets(string? offsets, int eventEndOffset) =>
        OffsetProblem(offsets, eventEndOffset) is null;

    /// <summary>What is wrong with the first bad offset, or null when every one is a distinct day of the event.</summary>
    internal static string? OffsetProblem(string? offsets, int eventEndOffset)
    {
        if (string.IsNullOrWhiteSpace(offsets)) return null;
        var seen = new HashSet<int>();
        foreach (var token in offsets.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var day))
                return $"'{token}' is not a day number";
            if (day < 0 || day > eventEndOffset)
                return $"day {day} is outside the event's days 0 to {eventEndOffset}";
            if (!seen.Add(day))
                return $"day {day} is listed twice";
        }
        return null;
    }

    internal static IReadOnlyList<Instant> GetOccurrenceInstants(
        Instant startAt, bool isRecurring, string? recurrenceDays,
        LocalDate? gateOpeningDate, DateTimeZone? timeZone, int? dayOffset = null)
    {
        if (gateOpeningDate is null || timeZone is null || !isRecurring || string.IsNullOrWhiteSpace(recurrenceDays))
            return [startAt];

        var startLocal = startAt.InZone(timeZone);
        return recurrenceDays
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? (int?)d : null)
            .Where(d => d.HasValue && (dayOffset == null || d == dayOffset))
            .Select(d => gateOpeningDate.Value.PlusDays(d!.Value)
                .At(startLocal.TimeOfDay)
                .InZoneLeniently(timeZone)
                .ToInstant())
            .ToList();
    }

    /// <summary>In offset order.</summary>
    public static string OffsetsToDisplayDays(string offsets, LocalDate gateOpeningDate)
    {
        var names = new List<string>();
        foreach (var part in offsets.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part.Trim(), CultureInfo.InvariantCulture, out var offset))
                names.Add(DayNames[((int)gateOpeningDate.PlusDays(offset).DayOfWeek - 1 + 7) % 7]);
        }
        return string.Join(" ", names);
    }

    /// <summary>Every matching offset within <c>0..eventEndOffset</c>; null when nothing matches.</summary>
    public static string? DisplayDaysToOffsets(string displayDays, LocalDate gateOpeningDate, int eventEndOffset)
    {
        var requested = displayDays.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var offsets = new List<int>();
        for (var offset = 0; offset <= eventEndOffset; offset++)
        {
            var name = DayNames[((int)gateOpeningDate.PlusDays(offset).DayOfWeek - 1 + 7) % 7];
            if (requested.Contains(name))
                offsets.Add(offset);
        }
        return offsets.Count > 0 ? string.Join(",", offsets) : null;
    }

    public static bool HasOnlyDisplayDays(string displayDays) =>
        displayDays.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(day => day.Trim())
            .All(day => DayNames.Contains(day, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Case-insensitive set comparison, so a lossless offsets→names→offsets round-trip is not mistaken for an edit.
    /// </summary>
    public static bool SameDays(string a, string b)
    {
        var setA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var setB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return setA.SetEquals(setB);
    }
}
