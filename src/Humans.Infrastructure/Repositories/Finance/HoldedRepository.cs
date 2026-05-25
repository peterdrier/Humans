using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Repositories.Finance;

internal sealed class HoldedRepository(IDbContextFactory<HumansDbContext> factory, ILogger<HoldedRepository> logger)
    : IHoldedRepository
{
    private readonly ILogger<HoldedRepository> _logger = logger;

    // ── Category map ─────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<HoldedCategoryMap>> GetCategoryMapAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedCategoryMap.AsNoTracking().ToListAsync(ct);
    }

    public async Task AddCategoryMapAsync(HoldedCategoryMap row, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.HoldedCategoryMap.Add(row);
        await ctx.SaveChangesAsync(ct);
    }

    // ── Docs ─────────────────────────────────────────────────────────────────

    public async Task UpsertDocsAsync(IReadOnlyList<HoldedExpenseDoc> docs, Instant now, CancellationToken ct = default)
    {
        if (docs.Count == 0) return;
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var ids = docs.Select(d => d.HoldedDocId).ToList();
        var existing = await ctx.HoldedExpenseDocs
            .Where(d => ids.Contains(d.HoldedDocId)).ToDictionaryAsync(d => d.HoldedDocId, ct);
        foreach (var d in docs)
        {
            if (existing.TryGetValue(d.HoldedDocId, out var cur))
            {
                cur.DocNumber = d.DocNumber;
                cur.ContactName = d.ContactName;
                cur.Date = d.Date;
                cur.Subtotal = d.Subtotal;
                cur.Tax = d.Tax;
                cur.Total = d.Total;
                cur.Currency = d.Currency;
                cur.ApprovedAt = d.ApprovedAt;
                cur.TagsJson = d.TagsJson;
                cur.BookedAccountId = d.BookedAccountId;
                cur.BudgetCategoryId = d.BudgetCategoryId;
                cur.MatchStatus = d.MatchStatus;
                cur.MatchSource = d.MatchSource;
                cur.RawPayload = d.RawPayload;
                cur.LastSyncedAt = now;
                cur.UpdatedAt = now;
            }
            else
            {
                d.LastSyncedAt = now;
                ctx.HoldedExpenseDocs.Add(d);
            }
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<HoldedExpenseDoc>> GetUnmatchedAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedExpenseDocs.AsNoTracking()
            .Where(d => d.MatchStatus == HoldedMatchStatus.Unmatched)
            // arch:db-sort-ok newest-first — unmatched review list, most recent docs surface first
            .OrderByDescending(d => d.Date)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<HoldedExpenseDoc>> GetMatchedForYearAsync(int calendarYear, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedExpenseDocs.AsNoTracking()
            .Where(d => d.MatchStatus == HoldedMatchStatus.Matched && d.Date.Year == calendarYear)
            .ToListAsync(ct);
    }

    // ── Sync state (singleton, seeded by migration) ──────────────────────────

    public async Task<HoldedSyncState> GetSyncStateAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedSyncStates.AsNoTracking().FirstAsync(s => s.Id == 1, ct);
    }

    public async Task SaveSyncStateAsync(HoldedSyncState state, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.HoldedSyncStates.FirstAsync(s => s.Id == 1, ct);
        ctx.Entry(existing).CurrentValues.SetValues(state);
        await ctx.SaveChangesAsync(ct);
    }
}
