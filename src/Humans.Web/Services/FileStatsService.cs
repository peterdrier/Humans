using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Web.Services;

/// <summary>Stored uploads totalled per UTC day and category folder, for the public metrics site.</summary>
public sealed class FileStatsService(IFileStorage storage)
{
    private const string UploadsPrefix = "uploads";

    /// <summary>
    /// One row per day from <paramref name="from"/> through <paramref name="through"/>: the files present at the
    /// end of that day by last write time. Files since deleted or overwritten are not reconstructed.
    /// </summary>
    public async Task<IReadOnlyList<FileStatsDay>> GetDailyAsync(LocalDate from, LocalDate through, CancellationToken ct = default)
    {
        var files = (await storage.ListAsync(UploadsPrefix, ct))
            .Select(f => (Category: CategoryOf(f.Key), f.Bytes, f.LastWrittenAt))
            .ToList();

        var days = new List<FileStatsDay>();
        for (var day = from; day <= through; day = day.PlusDays(1))
        {
            var endOfDay = day.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
            var present = files.Where(f => f.LastWrittenAt < endOfDay).ToList();
            days.Add(new FileStatsDay(day, present.Count, present.Sum(f => f.Bytes),
                present.GroupBy(f => f.Category, StringComparer.Ordinal)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => new FileStatsTotal(g.Count(), g.Sum(f => f.Bytes)), StringComparer.Ordinal)));
        }
        return days;
    }

    /// <summary>The folder under uploads/; the rest of a key names a person, camp or issue and never leaves.</summary>
    private static string CategoryOf(string key)
    {
        var segments = key.Split('/');
        return segments.Length > 2 ? segments[1] : "(root)";
    }
}

public sealed record FileStatsTotal(int Count, long Bytes);

public sealed record FileStatsDay(LocalDate Date, int Count, long Bytes, IReadOnlyDictionary<string, FileStatsTotal> Categories);
