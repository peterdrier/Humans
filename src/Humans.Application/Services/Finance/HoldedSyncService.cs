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

    /// <summary>
    /// Pull every purchase doc from Holded, resolve match status, upsert each
    /// into <c>holded_transactions</c>, and update the singleton sync state
    /// (Idle → Running → Idle on success, Running → Error on exception).
    /// If the state is already <see cref="HoldedSyncStatus.Running"/> when this
    /// is called, the call is a no-op and returns an empty result so two
    /// concurrent triggers (job + manual) do not race on Holded.
    /// </summary>
    public async Task<HoldedSyncResult> SyncAsync(CancellationToken ct = default)
    {
        var current = await _repository.GetSyncStateAsync(ct);
        if (current.SyncStatus == HoldedSyncStatus.Running)
        {
            _logger.LogInformation("Holded sync skipped: already running.");
            return new HoldedSyncResult(0, 0, 0, new Dictionary<string, int>(StringComparer.Ordinal));
        }

        var startedAt = _clock.GetCurrentInstant();
        await _repository.SetSyncStateAsync(HoldedSyncStatus.Running, startedAt, lastError: null, ct);

        try
        {
            var docs = await _client.GetAllPurchaseDocsAsync(ct);
            var transactions = new List<Domain.Entities.HoldedTransaction>(docs.Count);

            foreach (var (dto, rawJson) in docs)
            {
                var match = await ResolveMatchAsync(dto, ct);
                transactions.Add(BuildTransaction(dto, rawJson, match, startedAt));
            }

            await _repository.UpsertManyAsync(transactions, ct);

            var completedAt = _clock.GetCurrentInstant();
            await _repository.RecordSyncCompletedAsync(completedAt, docs.Count, ct);

            var byStatus = transactions
                .GroupBy(t => t.MatchStatus.ToString(), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var matched = transactions.Count(t => t.MatchStatus == HoldedMatchStatus.Matched);

            _logger.LogInformation(
                "Holded sync completed: {DocCount} docs, {Matched} matched, {Unmatched} unmatched.",
                docs.Count, matched, docs.Count - matched);

            return new HoldedSyncResult(docs.Count, matched, docs.Count - matched, byStatus);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Holded sync failed.");
            var errorAt = _clock.GetCurrentInstant();
            await _repository.SetSyncStateAsync(HoldedSyncStatus.Error, errorAt, ex.Message, ct);
            throw;
        }
    }

    private static Domain.Entities.HoldedTransaction BuildTransaction(
        HoldedDocDto dto, string rawJson, MatchOutcome match, Instant syncedAt)
    {
        return new Domain.Entities.HoldedTransaction
        {
            Id = Guid.NewGuid(),
            HoldedDocId = dto.Id,
            HoldedDocNumber = dto.DocNumber,
            ContactName = dto.ContactName ?? string.Empty,
            Date = Instant.FromUnixTimeSeconds(dto.Date).InZone(Madrid).Date,
            AccountingDate = dto.AccountingDate is null
                ? null
                : Instant.FromUnixTimeSeconds(dto.AccountingDate.Value).InZone(Madrid).Date,
            DueDate = dto.DueDate is null
                ? null
                : Instant.FromUnixTimeSeconds(dto.DueDate.Value).InZone(Madrid).Date,
            Subtotal = dto.Subtotal,
            Tax = dto.Tax,
            Total = dto.Total,
            PaymentsTotal = dto.PaymentsTotal,
            PaymentsPending = dto.PaymentsPending,
            PaymentsRefunds = dto.PaymentsRefunds,
            Currency = dto.Currency ?? "eur",
            ApprovedAt = dto.ApprovedAt is null
                ? null
                : Instant.FromUnixTimeSeconds(dto.ApprovedAt.Value),
            Tags = dto.Tags ?? new List<string>(),
            RawPayload = rawJson,
            SourceIncomingDocId = string.Equals(dto.From?.DocType, "incomingdocument", StringComparison.OrdinalIgnoreCase)
                ? dto.From?.Id
                : null,
            BudgetCategoryId = match.BudgetCategoryId,
            MatchStatus = match.Status,
            LastSyncedAt = syncedAt,
            CreatedAt = syncedAt,
            UpdatedAt = syncedAt,
        };
    }

    /// <summary>
    /// Manually reassign a Holded doc to a chosen <see cref="Domain.Entities.BudgetCategory"/>.
    /// The local DB write happens first so a Holded API failure never loses
    /// the assignment; the tag write-back to Holded is best-effort. Always
    /// records an audit log entry under <see cref="AuditAction.HoldedReassign"/>.
    /// </summary>
    public async Task<ReassignOutcome> ReassignAsync(
        string holdedDocId, Guid budgetCategoryId, Guid actorUserId, CancellationToken ct = default)
    {
        var category = await _budget.GetCategoryByIdAsync(budgetCategoryId);
        if (category is null)
            throw new InvalidOperationException($"BudgetCategory {budgetCategoryId} not found.");
        if (category.BudgetGroup is null)
            throw new InvalidOperationException(
                $"BudgetCategory {budgetCategoryId} loaded without BudgetGroup nav; cannot construct tag.");

        var tag = $"{category.BudgetGroup.Slug}-{category.Slug}";
        var now = _clock.GetCurrentInstant();

        // Local write first — source of truth. A Holded failure must not lose this.
        await _repository.AssignCategoryAsync(holdedDocId, budgetCategoryId, now, ct);

        var description = $"Reassigned Holded doc {holdedDocId} to category {budgetCategoryId} (tag={tag}).";
        await _audit.LogAsync(
            AuditAction.HoldedReassign,
            nameof(Domain.Entities.HoldedTransaction),
            budgetCategoryId,
            description,
            actorUserId);

        var pushed = await _client.TryAddTagAsync(holdedDocId, tag, ct);
        if (!pushed)
        {
            _logger.LogWarning(
                "Holded tag push failed for doc {HoldedDocId} (tag={Tag}); local match saved.",
                holdedDocId, tag);
            return new ReassignOutcome(
                LocalMatchSaved: true,
                TagPushedToHolded: false,
                Warning: "Tag could not be pushed to Holded — please add it manually.");
        }

        return new ReassignOutcome(LocalMatchSaved: true, TagPushedToHolded: true, Warning: null);
    }

    /// <summary>
    /// Project the singleton <c>HoldedSyncState</c> plus the live unmatched
    /// count into a single DTO for the /Finance dashboard card. Goes directly
    /// to the repository (no <c>HoldedTransactionService</c> indirection).
    /// </summary>
    public async Task<HoldedSyncDashboardDto> GetSyncDashboardAsync(CancellationToken ct = default)
    {
        var state = await _repository.GetSyncStateAsync(ct);
        var unmatched = await _repository.CountUnmatchedAsync(ct);
        return new HoldedSyncDashboardDto(
            state.LastSyncAt,
            state.SyncStatus,
            state.LastError,
            state.LastSyncedDocCount,
            unmatched);
    }
}
