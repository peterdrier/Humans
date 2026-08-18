namespace Humans.Users.Contracts;

// COVERAGE REDUCED (G5 lane 3b, nobodies-collective/Humans#866): dropped ": IApplicationService".
// Lost on the implementing class: HUM0027 (role-axis exclusivity). See Humans.Users.Contracts.csproj.
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
