namespace Humans.Users.Contracts;

// No marker interface — see Humans.Users.Contracts.csproj.
public interface IUserParticipationBackfillService
{
    Task<int> GetDefaultYearAsync(CancellationToken ct = default);
    Task<ParticipationBackfillResult> BackfillFromCsvAsync(int year, string? csvData, CancellationToken ct = default);
}

public sealed record ParticipationBackfillResult(bool Succeeded, string Message, int Count = 0)
{
    public static ParticipationBackfillResult Success(int count, int year) =>
        new(true, $"Successfully backfilled {count} participation records for {year}.", count);

    public static ParticipationBackfillResult Failure(string message) => new(false, message);
}
