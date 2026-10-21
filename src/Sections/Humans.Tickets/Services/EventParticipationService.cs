using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Gdpr.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
using Humans.Users.Contracts;
using NodaTime;
using EventParticipation = Humans.Tickets.Domain.EventParticipation;

namespace Humans.Tickets.Services;

/// <summary>
/// Event participation, owned by Tickets since Users decomposition commit 13
/// (<c>tickets_event_participations</c>): the reads other sections make through
/// <see cref="ITicketService"/>, Gate's write through <see cref="ITicketWrite"/>, the member's
/// declaration, ticket-sync reconciliation, the admin backfill, the Article 15/17 slice and the
/// account-merge fold.
/// <para>
/// Until <c>tickets.event-participations</c> has run, a user and year with no row here reads the
/// frozen legacy Users row through <see cref="UserInfo"/> (plan §3.1 step 4), and writes start from
/// that row. Removing a record that a legacy row backs leaves a cleared row so the legacy value never
/// comes back.
/// </para>
/// </summary>
internal sealed class EventParticipationService(
    IEventParticipationRepository repo,
    IUserService users,
    IAuditLogService audit,
    IClock clock,
    ILogger<EventParticipationService> logger)
    : IEventParticipationManagement, IUserDataContributor, IUserMerge
{
    internal const string EventParticipationsSlice = "EventParticipations";

    // ─── Reads ───────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<EventParticipationInfo>> GetParticipationsAsync(
        Guid userId, CancellationToken ct = default)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        var id = info?.Id ?? userId;
        return Effective(await repo.GetByUserIdAsync(id, ct), LegacyRows(info));
    }

    public async Task<IReadOnlyList<EventParticipationInfo>> GetAllParticipationsAsync(CancellationToken ct = default)
    {
        var legacy = (await users.GetAllRawUserInfosAsync(ct)).SelectMany(LegacyRows).ToList();
        return Effective(await repo.GetAllAsync(ct), legacy);
    }

    private static List<EventParticipationInfo> Effective(
        IReadOnlyList<EventParticipation> rows, IReadOnlyList<EventParticipation> legacy)
    {
        var shadowed = rows.Select(r => (r.UserId, r.Year)).ToHashSet();
        return [.. rows.Where(r => r.Status is not null)
            .Concat(legacy.Where(l => !shadowed.Contains((l.UserId, l.Year))))
            .OrderBy(r => r.Year)
            .Select(ToInfo)];
    }

    private static EventParticipationInfo ToInfo(EventParticipation r) =>
        new(r.UserId, r.Year, r.Status!.Value, r.Source, r.DeclaredAt, r.CheckedInAt);

    // ─── Writes ──────────────────────────────────────────────────────────────────

    public async Task<bool> DeclareNotAttendingAsync(Guid userId, int year, CancellationToken ct = default)
    {
        var written = await repo.UpsertAsync(
            userId, year, ParticipationStatus.NotAttending, ParticipationSource.UserDeclared,
            declaredAt: clock.GetCurrentInstant(), checkedInAt: null, await LegacyRowAsync(userId, year, ct), ct);

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
        var current = (await EffectiveForRawUserAsync(userId, ct)).FirstOrDefault(p => p.Year == year);
        if (current is not { Status: ParticipationStatus.NotAttending, Source: ParticipationSource.UserDeclared })
        {
            logger.LogWarning(
                "Cannot undo NotAttending for user {UserId} year {Year} — status is {Status} from {Source}",
                userId, year, current?.Status, current?.Source);
            return false;
        }

        var removed = await repo.ClearAsync(
            userId, year, ParticipationSource.UserDeclared, await LegacyRowAsync(userId, year, ct), ct);
        if (removed)
            logger.LogInformation("User {UserId} undid NotAttending declaration for year {Year}", userId, year);
        return removed;
    }

    public async Task SetParticipationFromTicketSyncAsync(
        Guid userId, int year, ParticipationStatus status, Instant? checkedInAt, CancellationToken ct = default) =>
        await repo.UpsertAsync(
            userId, year, status, ParticipationSource.TicketSync,
            declaredAt: null, checkedInAt, await LegacyRowAsync(userId, year, ct), ct);

    public async Task RemoveTicketSyncParticipationAsync(Guid userId, int year, CancellationToken ct = default) =>
        await repo.ClearAsync(
            userId, year, ParticipationSource.TicketSync, await LegacyRowAsync(userId, year, ct), ct);

    public async Task<int> BackfillParticipationsAsync(
        int year, IReadOnlyList<(Guid UserId, ParticipationStatus Status)> entries, Guid actorUserId,
        CancellationToken ct = default)
    {
        var ids = entries.Select(e => e.UserId).ToHashSet();
        var legacyByUser = (await users.GetAllRawUserInfosAsync(ct))
            .Where(u => ids.Contains(u.Id))
            .SelectMany(LegacyRows)
            .Where(l => l.Year == year)
            .ToDictionary(l => l.UserId);

        var changed = await repo.BackfillAsync(year, entries, legacyByUser, ct);
        foreach (var (userId, status) in changed)
        {
            await audit.LogAsync(AuditAction.ParticipationBackfilled, nameof(User), userId,
                $"Recorded {status} participation for {year} from admin backfill.", actorUserId);
        }

        logger.LogInformation("Backfilled {Count} participation records for year {Year}", changed.Count, year);
        return changed.Count;
    }

    // ─── tickets.event-participations ────────────────────────────────────────────

    public async Task<IReadOnlyList<EventParticipationInfo>> GetUncopiedLegacyParticipationsAsync(
        CancellationToken ct = default)
    {
        var present = (await repo.GetAllAsync(ct)).Select(r => (r.UserId, r.Year)).ToHashSet();
        return [.. (await users.GetAllRawUserInfosAsync(ct))
            .SelectMany(LegacyRows)
            .Where(l => !present.Contains((l.UserId, l.Year)))
            .OrderBy(l => l.Year).ThenBy(l => l.UserId)
            .Select(ToInfo)];
    }

    public async Task<int> CopyLegacyParticipationsAsync(CancellationToken ct = default)
    {
        var written = 0;
        foreach (var p in await GetUncopiedLegacyParticipationsAsync(ct))
        {
            var row = new EventParticipation
            {
                Id = Guid.NewGuid(),
                UserId = p.UserId,
                Year = p.Year,
                Status = p.Status,
                Source = p.Source,
                DeclaredAt = p.DeclaredAt,
                CheckedInAt = p.CheckedInAt,
            };
            if (await repo.TryInsertAsync(row, ct))
                written++;
        }
        return written;
    }

    // ─── Legacy ──────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<EventParticipationInfo>> EffectiveForRawUserAsync(Guid userId, CancellationToken ct) =>
        Effective(await repo.GetByUserIdAsync(userId, ct), LegacyRows(await users.GetRawUserInfoAsync(userId, ct)));

    private async Task<EventParticipation?> LegacyRowAsync(Guid userId, int year, CancellationToken ct) =>
        LegacyRows(await users.GetRawUserInfoAsync(userId, ct)).FirstOrDefault(l => l.Year == year);

    /// <summary>The one reader of the obsolete legacy slice: the read fallback, the write seed and the
    /// copy above. Deleted with the migration once production reports it done. Enum values map by name.</summary>
    private static IReadOnlyList<EventParticipation> LegacyRows(UserInfo? info)
    {
        if (info is null) return [];
#pragma warning disable HUM_USER_PARTICIPATIONS // the fallback and copy the field is kept for (plan §3.1)
        var legacy = info.EventParticipations;
#pragma warning restore HUM_USER_PARTICIPATIONS
        return [.. legacy.Select(l => new EventParticipation
        {
            Id = l.Id,
            UserId = info.Id,
            Year = l.Year,
            Status = Enum.Parse<ParticipationStatus>(l.Status.ToString()),
            Source = Enum.Parse<ParticipationSource>(l.Source.ToString()),
            DeclaredAt = l.DeclaredAt,
            CheckedInAt = l.CheckedInAt,
        })];
    }

    // ─── IUserMerge ──────────────────────────────────────────────────────────────

    public async Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct)
    {
        _ = actorUserId;
        _ = now;
        await repo.ReassignAsync(
            mergedFromUserId, mergedToUserId,
            LegacyRows(await users.GetRawUserInfoAsync(mergedFromUserId, ct)),
            LegacyRows(await users.GetRawUserInfoAsync(mergedToUserId, ct)),
            ct);
    }

    // ─── IUserDataContributor ────────────────────────────────────────────────────

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var rows = await GetParticipationsAsync(userId, ct);
        return
        [
            new UserDataSlice(EventParticipationsSlice, rows.Select(ep => new
            {
                ep.Year,
                Status = ep.Status.ToString(),
                Source = ep.Source.ToString(),
                DeclaredAt = ep.DeclaredAt.ToIso8601(),
                CheckedInAt = ep.CheckedInAt.ToIso8601(),
            }).ToList()),
        ];
    }

    // Retention unchanged from the Users declaration this slice came from: Users' erasure never
    // touched event_participations, and this keeps doing the same.
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
