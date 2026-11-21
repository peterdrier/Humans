using Humans.Development.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Development.Data;

internal sealed class DemoLedgerRepository(IDbContextFactory<DevelopmentDbContext> factory) : IDemoLedgerRepository
{
    public async Task<IReadOnlyList<DemoLedgerEntry>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.DemoLedger.AsNoTracking().ToListAsync(ct);
    }

    public async Task SaveAsync(DemoLedgerEntry entry, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var exists = await ctx.DemoLedger.AnyAsync(e => e.Id == entry.Id, ct);
        if (exists)
            ctx.DemoLedger.Update(entry);
        else
            ctx.DemoLedger.Add(entry);
        await ctx.SaveChangesAsync(ct);
    }
}
