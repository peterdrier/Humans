using Humans.Application.DTOs.Finance;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Finance;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Finance;

/// <summary>
/// Holded read-side sync orchestrator. Pulls purchase docs from Holded,
/// resolves each doc to a <c>BudgetCategory</c> via tag-slug matching,
/// upserts <c>HoldedTransaction</c> rows, and maintains the singleton
/// <c>HoldedSyncState</c>. Also handles manual reassignment with best-effort
/// tag write-back.
/// </summary>
public sealed class HoldedSyncService : IHoldedSyncService
{
    private static readonly DateTimeZone Madrid = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    private readonly IHoldedClient _client;
    private readonly IHoldedRepository _repository;
    private readonly IBudgetService _budget;
    private readonly IAuditLogService _audit;
    private readonly IClock _clock;
    private readonly ILogger<HoldedSyncService> _logger;

    public HoldedSyncService(
        IHoldedClient client,
        IHoldedRepository repository,
        IBudgetService budget,
        IAuditLogService audit,
        IClock clock,
        ILogger<HoldedSyncService> logger)
    {
        _client = client;
        _repository = repository;
        _budget = budget;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Pre-persistence resolution outcome for a single doc. Internal so tests
    /// in the same assembly can drive <see cref="ResolveMatchAsync"/> directly.
    /// </summary>
    internal sealed record MatchOutcome(HoldedMatchStatus Status, Guid? BudgetCategoryId);

    /// <summary>
    /// Apply the four match-resolution rules in order:
    /// <list type="number">
    ///   <item>Currency must be EUR (case-insensitive) — else <see cref="HoldedMatchStatus.UnsupportedCurrency"/>.</item>
    ///   <item>Doc date (accountingDate ?? date, in Europe/Madrid) must fall in an existing budget year — else <see cref="HoldedMatchStatus.NoBudgetYearForDate"/>.</item>
    ///   <item>Doc must have at least one tag — else <see cref="HoldedMatchStatus.NoTags"/>.</item>
    ///   <item>Tags resolve via "<c>group-slug-category-slug</c>" → all unresolved → <see cref="HoldedMatchStatus.UnknownTag"/>; one distinct category → <see cref="HoldedMatchStatus.Matched"/>; multiple distinct categories → <see cref="HoldedMatchStatus.MultiMatchConflict"/>.</item>
    /// </list>
    /// </summary>
    internal async Task<MatchOutcome> ResolveMatchAsync(HoldedDocDto doc, CancellationToken ct = default)
    {
        // Rule 1: currency
        if (!string.Equals(doc.Currency, "eur", StringComparison.OrdinalIgnoreCase))
            return new MatchOutcome(HoldedMatchStatus.UnsupportedCurrency, null);

        // Rule 2: budget year
        var date = ResolveDate(doc);
        var year = await _budget.GetYearForDateAsync(date, ct);
        if (year is null)
            return new MatchOutcome(HoldedMatchStatus.NoBudgetYearForDate, null);

        // Rule 3: any tags?
        if (doc.Tags is null || doc.Tags.Count == 0)
            return new MatchOutcome(HoldedMatchStatus.NoTags, null);

        // Rule 4: resolve each tag against the group-slug + category-slug index.
        var resolved = new HashSet<Guid>();
        foreach (var tag in doc.Tags)
        {
            var dash = tag.IndexOf('-', StringComparison.Ordinal);
            if (dash <= 0 || dash >= tag.Length - 1) continue; // malformed
            var groupSlug = tag[..dash];
            var categorySlug = tag[(dash + 1)..];
            var category = await _budget.GetCategoryBySlugAsync(year.Id, groupSlug, categorySlug, ct);
            if (category is not null) resolved.Add(category.Id);
        }

        return resolved.Count switch
        {
            0 => new MatchOutcome(HoldedMatchStatus.UnknownTag, null),
            1 => new MatchOutcome(HoldedMatchStatus.Matched, resolved.Single()),
            _ => new MatchOutcome(HoldedMatchStatus.MultiMatchConflict, null),
        };
    }

    private static LocalDate ResolveDate(HoldedDocDto doc)
    {
        var epoch = doc.AccountingDate ?? doc.Date;
        return Instant.FromUnixTimeSeconds(epoch).InZone(Madrid).Date;
    }

#pragma warning disable MA0025 // Filled in by subsequent tasks in this batch.
    public Task<HoldedSyncResult> SyncAsync(CancellationToken ct = default)
        => throw new NotImplementedException("Implemented in Task 12");

    public Task<ReassignOutcome> ReassignAsync(string holdedDocId, Guid budgetCategoryId, Guid actorUserId, CancellationToken ct = default)
        => throw new NotImplementedException("Implemented in Task 13");
#pragma warning restore MA0025
}
