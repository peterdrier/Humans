using Microsoft.EntityFrameworkCore;
using Humans.Users.Contracts;

namespace Humans.Users.Data.Repositories;

/// <summary>
/// EF-backed implementation of <see cref="IAccountMergeRepository"/>. The only
/// non-test file that touches <c>DbContext.AccountMergeRequests</c> after the
/// Profile-section §15 cleanup (issue #557).
/// Uses <see cref="IDbContextFactory{TContext}"/> so the repository can be
/// registered as Singleton while <c>UsersDbContext</c> remains Scoped.
/// </summary>
internal sealed class AccountMergeRepository(IDbContextFactory<UsersDbContext> factory) : IAccountMergeRepository
{
    public async Task<IReadOnlyList<AccountMergeRequest>> GetPendingAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.AccountMergeRequests
            .AsNoTracking()
            .Where(r => r.Status == AccountMergeRequestStatus.Pending)
            .ToListAsync(ct);
    }

    public async Task<AccountMergeRequest?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.AccountMergeRequests
            .FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<AccountMergeRequest?> GetByIdPlainAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.AccountMergeRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<IReadOnlyList<AccountMergeRequestGdprRow>> GetForUserGdprAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.AccountMergeRequests
            .AsNoTracking()
            .Where(amr => amr.TargetUserId == userId || amr.SourceUserId == userId)
            .Select(amr => new AccountMergeRequestGdprRow(
                amr.Status.ToString(),
                amr.TargetUserId == userId,
                amr.CreatedAt,
                amr.ResolvedAt))
            .ToListAsync(ct);
    }

    public async Task<int> ScrubPiiForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

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

    public async Task<IReadOnlySet<Guid>> GetPendingEmailIdsAsync(
        IReadOnlyList<Guid> emailIds, CancellationToken ct = default)
    {
        if (emailIds.Count == 0)
            return new HashSet<Guid>();

        await using var ctx = await factory.CreateDbContextAsync(ct);
        var ids = await ctx.AccountMergeRequests
            .Where(r => emailIds.Contains(r.PendingEmailId)
                && r.Status == AccountMergeRequestStatus.Pending)
            .Select(r => r.PendingEmailId)
            .ToListAsync(ct);

        return ids.ToHashSet();
    }

    public async Task<bool> HasPendingForUserAndEmailAsync(
        Guid targetUserId, string normalizedEmail, string? alternateEmail,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return alternateEmail is null
            ? await ctx.AccountMergeRequests.AnyAsync(
                r => r.TargetUserId == targetUserId
                    && EF.Functions.ILike(r.Email, normalizedEmail)
                    && r.Status == AccountMergeRequestStatus.Pending, ct)
            : await ctx.AccountMergeRequests.AnyAsync(
                r => r.TargetUserId == targetUserId
                    && (EF.Functions.ILike(r.Email, normalizedEmail) ||
                        EF.Functions.ILike(r.Email, alternateEmail))
                    && r.Status == AccountMergeRequestStatus.Pending, ct);
    }

    public async Task<bool> HasPendingForEmailIdAsync(Guid pendingEmailId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.AccountMergeRequests
            .AnyAsync(r => r.PendingEmailId == pendingEmailId
                && r.Status == AccountMergeRequestStatus.Pending, ct);
    }

    public async Task AddAsync(AccountMergeRequest request, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.AccountMergeRequests.Add(request);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(AccountMergeRequest request, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Attach(request);
        ctx.Entry(request).State = EntityState.Modified;
        await ctx.SaveChangesAsync(ct);
    }
}
