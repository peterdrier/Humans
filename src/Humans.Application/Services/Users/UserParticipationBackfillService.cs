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

    private static List<(Guid UserId, ParticipationStatus Status)> ParseEntries(string csvData)
    {
        var entries = new List<(Guid UserId, ParticipationStatus Status)>();
        var lines = csvData.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 2) continue;
            if (string.Equals(parts[0], "UserId", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Guid.TryParse(parts[0], out var userId)) continue;
            if (!Enum.TryParse<ParticipationStatus>(parts[1], ignoreCase: true, out var status)) continue;

            entries.Add((userId, status));
        }

        return entries;
    }
}
