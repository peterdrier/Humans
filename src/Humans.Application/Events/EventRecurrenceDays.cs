using System.Globalization;
using NodaTime;

namespace Humans.Application.Events;

/// <summary>
/// Converts between the stored recurrence representation — comma-separated
/// day-offsets from the burn gate date (e.g. <c>"0,2,4"</c>) — and the
/// human-friendly day-name form (<c>"Mon Wed Fri"</c>) used in the barrio
/// bulk-upload CSV.
/// </summary>
public static class EventRecurrenceDays
{
    private static readonly string[] DayNames = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    /// <summary>Offsets → space-separated day names, in offset order.</summary>
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

    /// <summary>
    /// Day names → every matching offset within <c>0..eventEndOffset</c>, as a
    /// comma-separated string (null when nothing matches).
    /// </summary>
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

    /// <summary>
    /// Case-insensitive set comparison of two space-separated day-name strings.
    /// Used for change-detection so a lossless offsets→names→offsets round-trip
    /// (e.g. a single Monday rendered as <c>"Mon"</c>) is not mistaken for an edit.
    /// </summary>
    public static bool SameDays(string a, string b)
    {
        var setA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var setB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return setA.SetEquals(setB);
    }
}
