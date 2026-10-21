using CsvHelper;
using Humans.Base.Csv;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
using NodaTime;

namespace Humans.Tickets.Services;

/// <summary>The admin participation backfill behind <c>/Tickets/Participation/Backfill</c>: parses pasted
/// <c>UserId,Status</c> rows, previews them, and on apply writes and audits through
/// <see cref="IEventParticipationManagement"/>.</summary>
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
            var previewCsv = "UserId,Status\n" + string.Join("\n", entries.Select(e => $"{e.UserId},{e.Status}"));
            return new ParticipationBackfillResult(true,
                $"Review {entries.Count} valid participation row(s) for {year} before importing.", entries.Count, previewCsv);
        }

        var count = await participations.BackfillParticipationsAsync(year, entries, actorUserId, ct);
        return ParticipationBackfillResult.Success(count, year);
    }

    /// <summary>
    /// Pasted rows of <c>UserId,Status</c> — an optional header row is skipped
    /// and rows without a defined status are silently dropped (the caller reports a count).
    /// </summary>
    private static List<(Guid UserId, ParticipationStatus Status)> ParseEntries(string csvData)
    {
        var config = HumansCsv.ReadConfig();
        config.HasHeaderRecord = false;

        var entries = new List<(Guid UserId, ParticipationStatus Status)>();
        using var reader = new StringReader(csvData);
        using var csv = new CsvReader(reader, config);

        while (csv.Read())
        {
            if (csv.Parser.Count < 2) continue;
            var first = csv.GetField(0);
            if (string.Equals(first, "UserId", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Guid.TryParse(first, out var userId)) continue;
            if (!Enum.TryParse<ParticipationStatus>(csv.GetField(1), ignoreCase: true, out var status) ||
                !Enum.IsDefined(status)) continue;

            entries.Add((userId, status));
        }

        return entries;
    }
}
