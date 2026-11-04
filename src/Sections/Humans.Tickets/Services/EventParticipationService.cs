using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Gdpr.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
using Humans.Users.Contracts;
using NodaTime;
using EventParticipation = Humans.Tickets.Domain.EventParticipation;

namespace Humans.Tickets.Services;

internal sealed class EventParticipationService(
    IEventParticipationRepository repo,
    IUserService users,
    IAuditLogService audit,
    IClock clock,
    ILogger<EventParticipationService> logger)
    : IEventParticipationManagement, IUserDataContributor, IUserMerge
{
    internal const string EventParticipationsSlice = "EventParticipations";

    public async Task<IReadOnlyList<EventParticipationInfo>> GetParticipationsAsync(
        Guid userId, CancellationToken ct = default)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        var id = info?.Id ?? userId;
        return ToInfos(await repo.GetByUserIdAsync(id, ct));
    }

    public async Task<IReadOnlyList<EventParticipationInfo>> GetAllParticipationsAsync(CancellationToken ct = default) =>
        ToInfos(await repo.GetAllAsync(ct));

    private static List<EventParticipationInfo> ToInfos(IReadOnlyList<EventParticipation> rows) =>
        [.. rows.Where(r => r.Status is not null).Select(ToInfo)];

    private static EventParticipationInfo ToInfo(EventParticipation r) =>
        new(r.UserId, r.Year, r.Status!.Value, r.Source, r.DeclaredAt, r.CheckedInAt);

    public async Task<bool> DeclareNotAttendingAsync(Guid userId, int year, CancellationToken ct = default)
    {
        var written = await repo.UpsertAsync(
            userId, year, ParticipationStatus.NotAttending, ParticipationSource.UserDeclared,
            declaredAt: clock.GetCurrentInstant(), checkedInAt: null, ct);

        if (!written)
        {
            logger.LogWarning(
                "Cannot declare NotAttending for user {UserId} year {Year} — already Attended", userId, year);
            return false;
        }

        logger.LogInformation("User {UserId} declared NotAttending for year {Year}", userId, year);
        return true;
    }

    public async Task<bool> UndoNotAttendingAsync(Guid userId, int year, CancellationToken ct = default)
    {
        var current = (await repo.GetByUserIdAsync(userId, ct)).FirstOrDefault(p => p.Year == year);
        if (current is not { Status: ParticipationStatus.NotAttending, Source: ParticipationSource.UserDeclared })
        {
            logger.LogWarning(
                "Cannot undo NotAttending for user {UserId} year {Year} — status is {Status} from {Source}",
                userId, year, current?.Status, current?.Source);
            return false;
        }

        var removed = await repo.ClearAsync(
            userId, year, ParticipationSource.UserDeclared, ct);
        if (removed)
            logger.LogInformation("User {UserId} undid NotAttending declaration for year {Year}", userId, year);
        return removed;
    }

    public async Task RecordParticipationAsync(
        Guid userId, int year, ParticipationStatus status, Instant? checkedInAt, CancellationToken ct = default) =>
        await repo.UpsertAsync(
            userId, year, status, ParticipationSource.TicketSync,
            declaredAt: null, checkedInAt, ct);

    public async Task RemoveTicketSyncParticipationAsync(Guid userId, int year, CancellationToken ct = default) =>
        await repo.ClearAsync(
            userId, year, ParticipationSource.TicketSync, ct);

    public async Task<int> BackfillParticipationsAsync(
        int year, IReadOnlyList<(Guid UserId, ParticipationStatus Status)> entries, Guid actorUserId,
        CancellationToken ct = default)
    {
        var changed = await repo.BackfillAsync(year, entries, ct);
        foreach (var (userId, status) in changed)
        {
            await audit.LogAsync(TicketsAuditActions.ParticipationBackfilled, "User", userId,
                $"Recorded {status} participation for {year} from admin backfill.", actorUserId);
        }

        logger.LogInformation("Backfilled {Count} participation records for year {Year}", changed.Count, year);
        return changed.Count;
    }

    public async Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct)
    {
        _ = actorUserId;
        _ = now;
        await repo.ReassignAsync(mergedFromUserId, mergedToUserId, ct);
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var rows = await GetParticipationsAsync(userId, ct);
        return
        [
            new UserDataSlice(EventParticipationsSlice, rows.OrderBy(ep => ep.Year).Select(ep => new
            {
                ep.Year,
                Status = ep.Status.ToString(),
                Source = ep.Source.ToString(),
                DeclaredAt = ep.DeclaredAt.ToIso8601(),
                CheckedInAt = ep.CheckedInAt.ToIso8601(),
            }).ToList()),
        ];
    }

    // Retained as Users declared it: Users' erasure never touched these rows either.
    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [EventParticipationsSlice] =
                "Retained: the year and status of each participation, keyed to the tombstoned " +
                "user id, is the association's register of who was a member in which year — " +
                "Ley Orgánica 1/2002 Art. 14, GDPR Art. 17(3)(b). It carries no identity fields.",
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>Nothing to erase: the slice is retained (see <see cref="ErasureDeclaration"/>).</summary>
    public Task EraseForUserAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;
}
