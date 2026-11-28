using Microsoft.EntityFrameworkCore;
using Humans.Base.Data;

namespace Humans.Users.Data.Repositories;

internal sealed class AccountMergeRepository(IDbContextFactory<UsersDbContext> factory) : SectionRepository<UsersDbContext>(factory), IAccountMergeRepository
{
    public async Task<IReadOnlyList<AccountMergeRequest>> GetPendingAsync(CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.AccountMergeRequests
            .AsNoTracking()
            .Where(r => r.Status == AccountMergeRequestStatus.Pending)
            .ToListAsync(ct), ct);

    public async Task<AccountMergeRequest?> GetByIdPlainAsync(Guid id, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.AccountMergeRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, ct), ct);

    public async Task<IReadOnlyList<AccountMergeRequestGdprRow>> GetForUserGdprAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.AccountMergeRequests
            .AsNoTracking()
            .Where(amr => userIds.Contains(amr.TargetUserId) || userIds.Contains(amr.SourceUserId))
            .Select(amr => new AccountMergeRequestGdprRow(
                amr.Status.ToString(),
                userIds.Contains(amr.TargetUserId),
                amr.CreatedAt,
                amr.ResolvedAt))
            .ToListAsync(ct), ct);

    public async Task<int> ScrubPiiForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);

        var rows = await ctx.AccountMergeRequests
            .Where(amr => amr.TargetUserId == userId
                       || amr.SourceUserId == userId
                       || amr.ResolvedByUserId == userId)
            .ToListAsync(ct);
        if (rows.Count == 0) return 0;

        foreach (var row in rows)
        {
            // Email and AdminNotes describe the human being merged, not the admin
            // who resolved it — scrubbing them on a row this user merely resolved
            // would erase someone else's record. Drop the attribution instead.
            if (row.TargetUserId == userId || row.SourceUserId == userId)
            {
                // Email is init-only — write through the tracked entry.
                ctx.Entry(row).Property(nameof(AccountMergeRequest.Email)).CurrentValue = string.Empty;
                row.AdminNotes = null;
            }

            if (row.ResolvedByUserId == userId)
            {
                row.ResolvedByUserId = null;
            }
        }

        await ctx.SaveChangesAsync(ct);
        return rows.Count;
    }

    public async Task UpdateAsync(AccountMergeRequest request, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        ctx.Attach(request);
        ctx.Entry(request).State = EntityState.Modified;
        await ctx.SaveChangesAsync(ct);
    }
}
