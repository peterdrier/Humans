using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Repositories;

/// <summary>
/// EF-backed implementation of <see cref="IConsentRepository"/>. The only
/// non-test file that touches <c>DbContext.ConsentRecords</c> after the
/// ConsentService migration lands (issue #547). Uses
/// <see cref="IDbContextFactory{TContext}"/> so the repository can be
/// registered as Singleton while <c>HumansDbContext</c> remains Scoped.
/// </summary>
/// <remarks>
/// <c>consent_records</c> is append-only per design-rules §12 — only
/// <see cref="AddAsync"/> is exposed; there are no <c>UpdateAsync</c> or
/// <c>DeleteAsync</c>. Database triggers reject any UPDATE/DELETE at the
/// storage layer as a secondary defense.
/// </remarks>
public sealed class ConsentRepository : IConsentRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public ConsentRepository(IDbContextFactory<HumansDbContext> factory)
    {
        _factory = factory;
    }

    // ==========================================================================
    // Writes — append-only
    // ==========================================================================

    public async Task AddAsync(ConsentRecord record, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.ConsentRecords.Add(record);
        await ctx.SaveChangesAsync(ct);
    }

    // ==========================================================================
    // Reads
    // ==========================================================================

    public async Task<bool> ExistsForUserAndVersionAsync(
        Guid userId, Guid documentVersionId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ConsentRecords
            .AsNoTracking()
            .AnyAsync(c => c.UserId == userId && c.DocumentVersionId == documentVersionId, ct);
    }

    public async Task<ConsentRecord?> GetByUserAndVersionAsync(
        Guid userId, Guid documentVersionId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ConsentRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.UserId == userId && c.DocumentVersionId == documentVersionId, ct);
    }

    public async Task<IReadOnlyList<ConsentRecord>> GetAllForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ConsentRecords
            .AsNoTracking()
            .Include(c => c.DocumentVersion)
                .ThenInclude(v => v.LegalDocument)
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.ConsentedAt)
            .ToListAsync(ct);
    }

    public async Task<int> GetCountForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ConsentRecords
            .CountAsync(c => c.UserId == userId, ct);
    }

    public async Task<IReadOnlySet<Guid>> GetExplicitlyConsentedVersionIdsAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var ids = await ctx.ConsentRecords
            .AsNoTracking()
            .Where(c => c.UserId == userId && c.ExplicitConsent)
            .Select(c => c.DocumentVersionId)
            .ToListAsync(ct);

        return ids.ToHashSet();
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>> GetExplicitlyConsentedVersionIdsForUsersAsync(
        IReadOnlyList<Guid> userIds, CancellationToken ct = default)
    {
        var result = userIds.ToDictionary(
            id => id,
            _ => (IReadOnlySet<Guid>)new HashSet<Guid>());

        if (userIds.Count == 0)
            return result;

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var pairs = await ctx.ConsentRecords
            .AsNoTracking()
            .Where(c => userIds.Contains(c.UserId) && c.ExplicitConsent)
            .Select(c => new { c.UserId, c.DocumentVersionId })
            .ToListAsync(ct);

        foreach (var pair in pairs)
        {
            ((HashSet<Guid>)result[pair.UserId]).Add(pair.DocumentVersionId);
        }

        return result;
    }

    public async Task<IReadOnlyList<(Guid UserId, Guid DocumentVersionId)>> GetPairsForUsersAndVersionsAsync(
        IReadOnlyCollection<Guid> userIds,
        IReadOnlyCollection<Guid> documentVersionIds,
        CancellationToken ct = default)
    {
        if (userIds.Count == 0 || documentVersionIds.Count == 0)
            return [];

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var pairs = await ctx.ConsentRecords
            .AsNoTracking()
            .Where(c => userIds.Contains(c.UserId) && documentVersionIds.Contains(c.DocumentVersionId))
            .Select(c => new { c.UserId, c.DocumentVersionId })
            .ToListAsync(ct);

        return pairs.Select(p => (p.UserId, p.DocumentVersionId)).ToList();
    }
}
