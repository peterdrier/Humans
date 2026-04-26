using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using NodaTime;

namespace Humans.Infrastructure.Repositories.Finance;

/// <summary>
/// EF-backed implementation of <see cref="IHoldedRepository"/>. Per design-rules §15b:
/// Singleton, IDbContextFactory-based, fresh DbContext per method.
/// </summary>
public sealed class HoldedRepository : IHoldedRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;
    private readonly ILogger<HoldedRepository> _logger;

    public HoldedRepository(IDbContextFactory<HumansDbContext> factory, ILogger<HoldedRepository> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<HoldedSyncState> GetSyncStateAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var state = await ctx.HoldedSyncStates.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        return state ?? throw new InvalidOperationException("HoldedSyncState singleton (Id=1) is missing — migration seed failed.");
    }

    public async Task SetSyncStateAsync(HoldedSyncStatus status, Instant changedAt, string? lastError, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var state = await ctx.HoldedSyncStates.FirstAsync(s => s.Id == 1, ct);
        state.SyncStatus = status;
        state.StatusChangedAt = changedAt;
        state.LastError = lastError;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task RecordSyncCompletedAsync(Instant completedAt, int docCount, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var state = await ctx.HoldedSyncStates.FirstAsync(s => s.Id == 1, ct);
        state.SyncStatus = HoldedSyncStatus.Idle;
        state.LastError = null;
        state.LastSyncAt = completedAt;
        state.LastSyncedDocCount = docCount;
        state.StatusChangedAt = completedAt;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpsertAsync(HoldedTransaction tx, CancellationToken ct = default)
    {
        await UpsertManyAsync(new[] { tx }, ct);
    }

    public async Task UpsertManyAsync(IReadOnlyList<HoldedTransaction> transactions, CancellationToken ct = default)
    {
        if (transactions.Count == 0) return;
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var ids = transactions.Select(t => t.HoldedDocId).ToList();
        var existing = await ctx.HoldedTransactions
            .Where(t => ids.Contains(t.HoldedDocId))
            .ToDictionaryAsync(t => t.HoldedDocId, ct);

        foreach (var incoming in transactions)
        {
            if (existing.TryGetValue(incoming.HoldedDocId, out var current))
            {
                current.HoldedDocNumber = incoming.HoldedDocNumber;
                current.ContactName = incoming.ContactName;
                current.Date = incoming.Date;
                current.AccountingDate = incoming.AccountingDate;
                current.DueDate = incoming.DueDate;
                current.Subtotal = incoming.Subtotal;
                current.Tax = incoming.Tax;
                current.Total = incoming.Total;
                current.PaymentsTotal = incoming.PaymentsTotal;
                current.PaymentsPending = incoming.PaymentsPending;
                current.PaymentsRefunds = incoming.PaymentsRefunds;
                current.Currency = incoming.Currency;
                current.ApprovedAt = incoming.ApprovedAt;
                current.Tags = incoming.Tags;
                current.RawPayload = incoming.RawPayload;
                current.SourceIncomingDocId = incoming.SourceIncomingDocId;
                current.BudgetCategoryId = incoming.BudgetCategoryId;
                current.MatchStatus = incoming.MatchStatus;
                current.LastSyncedAt = incoming.LastSyncedAt;
                current.UpdatedAt = incoming.LastSyncedAt;
                // CreatedAt preserved.
            }
            else
            {
                ctx.HoldedTransactions.Add(incoming);
            }
        }

        await ctx.SaveChangesAsync(ct);
    }

    public async Task<HoldedTransaction?> GetByHoldedDocIdAsync(string holdedDocId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.HoldedTransactions.AsNoTracking()
            .FirstOrDefaultAsync(t => t.HoldedDocId == holdedDocId, ct);
    }

    public async Task<IReadOnlyList<HoldedTransaction>> GetUnmatchedAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.HoldedTransactions.AsNoTracking()
            .Where(t => t.MatchStatus != HoldedMatchStatus.Matched)
            .OrderByDescending(t => t.AccountingDate ?? t.Date)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<HoldedTransaction>> GetByCategoryAsync(Guid budgetCategoryId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.HoldedTransactions.AsNoTracking()
            .Where(t => t.BudgetCategoryId == budgetCategoryId)
            .OrderByDescending(t => t.AccountingDate ?? t.Date)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, decimal>> GetActualSumsByCategoryAsync(Guid budgetYearId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        // Sum Total per BudgetCategory across approved transactions whose date falls in the year.
        // Year-membership is enforced by BudgetService.GetYearForDateAsync at sync-time;
        // here we filter through the BudgetCategory → BudgetGroup → BudgetYearId chain.
        var sums = await ctx.HoldedTransactions.AsNoTracking()
            .Where(t => t.ApprovedAt != null && t.BudgetCategoryId != null)
            .Join(
                ctx.BudgetCategories.AsNoTracking(),
                t => t.BudgetCategoryId,
                c => c.Id,
                (t, c) => new { c.Id, c.BudgetGroupId, t.Total })
            .Join(
                ctx.BudgetGroups.AsNoTracking().Where(g => g.BudgetYearId == budgetYearId),
                tc => tc.BudgetGroupId,
                g => g.Id,
                (tc, _) => new { tc.Id, tc.Total })
            .GroupBy(x => x.Id)
            .Select(g => new { CategoryId = g.Key, Sum = g.Sum(x => x.Total) })
            .ToListAsync(ct);

        return sums.ToDictionary(s => s.CategoryId, s => s.Sum);
    }

    public async Task<int> CountUnmatchedAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.HoldedTransactions
            .CountAsync(t => t.MatchStatus != HoldedMatchStatus.Matched, ct);
    }

    public async Task AssignCategoryAsync(string holdedDocId, Guid budgetCategoryId, Instant updatedAt, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var tx = await ctx.HoldedTransactions.FirstOrDefaultAsync(t => t.HoldedDocId == holdedDocId, ct)
            ?? throw new InvalidOperationException($"HoldedTransaction not found: {holdedDocId}");
        tx.BudgetCategoryId = budgetCategoryId;
        tx.MatchStatus = HoldedMatchStatus.Matched;
        tx.UpdatedAt = updatedAt;
        await ctx.SaveChangesAsync(ct);
    }
}
