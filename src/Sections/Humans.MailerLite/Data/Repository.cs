using Humans.MailerLite.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.MailerLite.Data;

internal sealed class Repository(IDbContextFactory<MailerLiteDbContext> factory)
    : IMailerLiteRepository
{
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
