using Humans.Base.Interfaces;

namespace Humans.Tickets.Services;

internal interface IUserParticipationBackfillService : IApplicationService
{
    Task<int> GetDefaultYearAsync(CancellationToken ct = default);
    /// <summary>Previews parsed rows by default; an explicit apply writes and audits changed records.</summary>
    Task<ParticipationBackfillResult> BackfillFromCsvAsync(int year, string? csvData, Guid actorUserId, CancellationToken ct = default, bool apply = false);
}

internal sealed record ParticipationBackfillResult(bool Succeeded, string Message, int Count = 0, string? PreviewCsv = null)
{
    public static ParticipationBackfillResult Success(int count, int year) =>
        new(true, $"Successfully backfilled {count} participation records for {year}.", count);

    public static ParticipationBackfillResult Failure(string message) => new(false, message);
}
