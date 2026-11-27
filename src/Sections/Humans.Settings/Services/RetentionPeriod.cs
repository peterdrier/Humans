using System.Globalization;

namespace Humans.Settings.Services;

/// <summary>The app-wide retention period: how long temporary data is kept before the daily job deletes it.</summary>
internal static class RetentionPeriod
{
    /// <summary>Persisted key: never rename it.</summary>
    public const string Key = "Retention:Days";

    /// <summary>One full event cycle.</summary>
    public const int DefaultDays = 365;

    /// <summary>Absent or unreadable falls back to <see cref="DefaultDays"/>; saves only ever store a whole number of 1 or more.</summary>
    public static int Parse(string? stored) =>
        int.TryParse(stored, NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days > 0
            ? days
            : DefaultDays;
}
