using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories.Mailer;

public sealed class ForgottenEmailRepository : IForgottenEmailRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public ForgottenEmailRepository(IDbContextFactory<HumansDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<bool> ExistsByHashAsync(string emailHash, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ForgottenEmails.AsNoTracking()
            .AnyAsync(f => f.EmailHash == emailHash, ct);
    }

    public async Task<IReadOnlySet<string>> GetExistingHashesAsync(
        IReadOnlyCollection<string> hashes, CancellationToken ct = default)
    {
        if (hashes.Count == 0) return new HashSet<string>(StringComparer.Ordinal);
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var hashList = hashes.ToList();
        var found = await ctx.ForgottenEmails.AsNoTracking()
            .Where(f => hashList.Contains(f.EmailHash))
            .Select(f => f.EmailHash)
            .ToListAsync(ct);
        return new HashSet<string>(found, StringComparer.Ordinal);
    }

    public async Task<int> AddManyAsync(
        Guid userId, IReadOnlyCollection<string> emailHashes,
        Instant anonymizedAt, CancellationToken ct = default)
    {
        if (emailHashes.Count == 0) return 0;
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var hashList = emailHashes.ToList();
        var existing = await ctx.ForgottenEmails.AsNoTracking()
            .Where(f => f.UserId == userId && hashList.Contains(f.EmailHash))
            .Select(f => f.EmailHash)
            .ToListAsync(ct);
        var existingSet = new HashSet<string>(existing, StringComparer.Ordinal);
        var newRows = hashList
            .Where(h => !existingSet.Contains(h))
            .Select(h => new ForgottenEmail
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                EmailHash = h,
                AnonymizedAt = anonymizedAt
            })
            .ToList();
        if (newRows.Count == 0) return 0;
        ctx.ForgottenEmails.AddRange(newRows);
        await ctx.SaveChangesAsync(ct);
        return newRows.Count;
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ForgottenEmails.AsNoTracking().CountAsync(ct);
    }
}
