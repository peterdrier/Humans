using CsvHelper;
using Humans.Application.Csv;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Services.Users;

public sealed class UserParticipationBackfillService(
    IUserService userService,
    IShiftManagementService shiftManagementService,
    IClock clock) : IUserParticipationBackfillService
{
    public async Task<int> GetDefaultYearAsync(CancellationToken ct = default)
    {
        var activeEvent = await shiftManagementService.GetActiveAsync();
        return activeEvent?.Year ?? clock.GetCurrentInstant().InUtc().Year;
    }

    public async Task<ParticipationBackfillResult> BackfillFromCsvAsync(
        int year,
        string? csvData,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(csvData))
            return ParticipationBackfillResult.Failure("Please provide CSV data with UserId and Status columns.");

        var entries = ParseEntries(csvData);
        if (entries.Count == 0)
            return ParticipationBackfillResult.Failure("No valid entries found in the CSV data.");

        var count = await userService.BackfillParticipationsAsync(year, entries, ct);
        return ParticipationBackfillResult.Success(count, year);
    }

    /// <summary>
    /// Pasted rows of <c>UserId,Status</c> — an optional header row is skipped
    /// and unparseable rows are silently dropped (the caller reports a count).
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
            if (!Enum.TryParse<ParticipationStatus>(csv.GetField(1), ignoreCase: true, out var status)) continue;

            entries.Add((userId, status));
        }

        return entries;
    }
}
