using System.Globalization;
using CsvHelper;
using Humans.Base.Csv;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
using NodaTime;

namespace Humans.Tickets.Services;

internal sealed class UserParticipationBackfillService(
    IEventParticipationManagement participations,
    ISettingsService settingsService,
    IClock clock) : IUserParticipationBackfillService
{
    public async Task<int> GetDefaultYearAsync(CancellationToken ct = default)
    {
        var activeEvent = await settingsService.GetActiveEventSettingsAsync(ct);
        return activeEvent?.Year ?? clock.GetCurrentInstant().InUtc().Year;
    }

    public async Task<ParticipationBackfillResult> BackfillFromCsvAsync(
        int year,
        string? csvData,
        Guid actorUserId,
        CancellationToken ct = default,
        bool apply = false)
    {
        if (string.IsNullOrWhiteSpace(csvData))
            return ParticipationBackfillResult.Failure("Please provide CSV data with UserId and Status columns.");

        var entries = ParseEntries(csvData);
        if (entries.Count == 0)
            return ParticipationBackfillResult.Failure("No valid entries found in the CSV data.");

        if (!apply)
        {
            using var preview = new StringWriter(CultureInfo.InvariantCulture);
            using (var csv = CsvWriter.Create(preview, HumansCsv.WriteConfig(), leaveOpen: true))
            {
                csv.WriteRow("UserId", "Status");
                foreach (var entry in entries)
                    csv.WriteRow(entry.UserId, entry.Status);
            }
            var previewCsv = preview.ToString();
            return new ParticipationBackfillResult(true,
                $"Review {entries.Count} valid participation row(s) for {year} before importing.", entries.Count, previewCsv);
        }

        var count = await participations.BackfillParticipationsAsync(year, entries, actorUserId, ct);
        return ParticipationBackfillResult.Success(count, year);
    }

    /// <summary>An optional header row is skipped; rows without a defined status are dropped (the caller reports a
    /// count).</summary>
    private static List<(Guid UserId, ParticipationStatus Status)> ParseEntries(string csvData)
    {
        var entries = new List<(Guid UserId, ParticipationStatus Status)>();
        using var csv = CsvReader.FromText(csvData, HumansCsv.ReadConfig() with { HasHeader = false });

        while (csv.Read())
        {
            if (csv.FieldCount < 2) continue;
            var first = csv.GetString(0);
            if (!Guid.TryParse(first, out var userId)) continue;
            if (!Enum.TryParse<ParticipationStatus>(csv.GetString(1), ignoreCase: true, out var status) ||
                !Enum.IsDefined(status)) continue;

            entries.Add((userId, status));
        }

        return entries;
    }
}
