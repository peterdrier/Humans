using Humans.Base.Threading;
using Humans.MailerLite.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.MailerLite.Data;

internal sealed class Repository(
    IDbContextFactory<MailerLiteDbContext> factory,
    ILogger<Repository> logger) : IMailerLiteRepository
{
    // The daily sync job and an admin's "Sync Audience" click can hit the same key at once,
    // and a bare read-then-insert would write that key twice. Striped like UserService's
    // profile-stub locks; single server, so an app-level lock is the whole answer.
    private static readonly TrackedLock[] UpsertLocks = Enumerable
        .Range(0, 8)
        .Select(i => new TrackedLock($"MailerLite.SyncStateUpsert[{i}]"))
        .ToArray();

    private static TrackedLock UpsertLockFor(string key) =>
        UpsertLocks[(uint)StringComparer.Ordinal.GetHashCode(key) % (uint)UpsertLocks.Length];

    public async Task<IReadOnlyList<MailerLiteSyncState>> GetSyncStatesAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.MailerLiteSyncStates.AsNoTracking().ToListAsync(ct);
    }

    public async Task<MailerLiteSyncState?> GetSyncStateAsync(
        string key, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.MailerLiteSyncStates
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == key, ct);
    }

    public async Task<MailerLiteSyncState> UpsertSyncStateAsync(
        MailerLiteSyncState state, CancellationToken ct = default)
    {
        using var _ = await UpsertLockFor(state.Key).AcquireAsync(logger, ct);

        await using var ctx = await factory.CreateDbContextAsync(ct);

        var row = await ctx.MailerLiteSyncStates.FirstOrDefaultAsync(s => s.Key == state.Key, ct);
        if (row is null)
        {
            row = new MailerLiteSyncState { Id = Guid.NewGuid(), Key = state.Key };
            ctx.MailerLiteSyncStates.Add(row);
        }

        row.LastSyncAt = state.LastSyncAt;
        row.Summary = state.Summary;
        row.GroupId = state.GroupId;
        row.GroupName = state.GroupName;
        row.Candidates = state.Candidates;
        row.ExcludedUnsubscribed = state.ExcludedUnsubscribed;
        row.Created = state.Created;
        row.Assigned = state.Assigned;
        row.AlreadyAssigned = state.AlreadyAssigned;
        row.Unassigned = state.Unassigned;
        row.Errors = state.Errors;

        await ctx.SaveChangesAsync(ct);
        return row;
    }
}
