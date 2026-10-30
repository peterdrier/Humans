using System.Text.Json;
using Humans.Base.Interfaces;
using Humans.Finance.Contracts;
using Humans.Finance.Data;
using Humans.Finance.Domain;
using Humans.Holded.Contracts;
using NodaTime;

namespace Humans.Finance.Services;

internal sealed class HoldedDocumentSync(
    IHoldedRepository repo,
    IHoldedClient client,
    IClock clock,
    ILogger logger) : IApplicationService
{
    private static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    /// <summary>Replaces the mirror.</summary>
    /// <remarks><paramref name="checkDocs"/> runs over the fetched documents before the mirror is written. It must not
    /// throw.</remarks>
    public async Task<HoldedSyncResult> SyncAsync(
        Func<IReadOnlyList<HoldedPurchaseDocListItemDto>, CancellationToken, Task> checkDocs,
        CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();

        var state = await repo.GetOrCreateDocSyncStateAsync(ct);
        state.Status = "Running";
        state.StatusChangedAt = now;
        await repo.SaveDocSyncStateAsync(state, ct);

        try
        {
            var map = await repo.GetCategoryMapAsync(ct);
            var entries = map
                .Where(m => m.IsActive)
                .Select(m => new HoldedMatchEntry(m.BudgetCategoryId, m.HoldedAccountId, m.Tag))
                .ToArray();

            var managedIds = (await repo.GetManagedAccountsAsync(ct))
                .Select(m => m.HoldedAccountId)
                .ToHashSet(StringComparer.Ordinal);

            var allDocs = await client.ListPurchaseDocumentsAsync(ct);

            var docs = allDocs.Select(doc => MapDoc(doc, entries, managedIds, now)).ToList();

            await checkDocs(allDocs, ct);

            await FetchNotesAsync(docs, ct);

            await repo.ReplaceDocsAsync(docs, now, ct);

            var matched = docs.Count(d => d.MatchStatus == HoldedMatchStatus.Matched);
            var unmatched = docs.Count(d => d.MatchStatus == HoldedMatchStatus.Unmatched);

            state.Status = "Idle";
            state.LastSyncAt = now;
            state.StatusChangedAt = now;
            state.LastError = null;
            state.LastSyncedDocCount = docs.Count;
            await repo.SaveDocSyncStateAsync(state, ct);

            return new HoldedSyncResult(docs.Count, matched, unmatched);
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
                logger.LogWarning("Holded document sync cancelled by the caller");
            else
                logger.LogError(ex, "Service.SyncAsync failed");
            state.Status = "Error";
            state.LastError = ex.Message;
            if (state.LastError.Length > 2000)
            {
                var length = 2000;
                if (char.IsHighSurrogate(state.LastError[length - 1]) && char.IsLowSurrogate(state.LastError[length]))
                    length--;
                state.LastError = state.LastError[..length];
            }
            state.StatusChangedAt = now;
            try { await repo.SaveDocSyncStateAsync(state, CancellationToken.None); }
            catch (Exception saveEx) { logger.LogError(saveEx, "Failed to persist error sync state"); }
            throw;
        }
    }

    // The plan allows ~2,000 Holded calls a month: 50 a night is at most ~1,500, leaving the rest for the ledger sync,
    // the list call and the pushes. A backlog drains over several nights.
    private const int MaxNotesFetchesPerRun = 50;

    /// <summary>
    /// One GET per push-written document (description <c>ER: </c>) whose notes the mirror lacks, at most
    /// <see cref="MaxNotesFetchesPerRun"/> a run; the rest keep theirs through the upsert. Other documents are never
    /// fetched and keep null notes. A failed GET leaves the notes unknown for the next sync to retry and never fails
    /// this one; a transient failure ends the run's fetching.
    /// </summary>
    private async Task FetchNotesAsync(IReadOnlyList<HoldedExpenseDoc> docs, CancellationToken ct)
    {
        var known = (await repo.GetAllDocsAsync(ct))
            .Where(d => d.Notes is not null)
            .Select(d => d.HoldedDocId)
            .ToHashSet(StringComparer.Ordinal);
        var candidates = docs
            .Where(d => !known.Contains(d.HoldedDocId)
                        && d.Description?.StartsWith(ExpenseRecon.DescriptionMarker, StringComparison.Ordinal) == true)
            .Take(MaxNotesFetchesPerRun);
        var failed = 0;
        foreach (var doc in candidates)
        {
            try
            {
                doc.Notes = (await client.GetPurchaseDocumentAsync(doc.HoldedDocId, ct)).Notes?.Trim() ?? "";
            }
            catch (HoldedTransientException ex)
            {
                failed++;
                logger.LogDebug(ex, "Holded purchase document {DocId} could not be read for its notes", doc.HoldedDocId);
                break;
            }
            catch (Exception ex) when (ex is HoldedPermanentException or JsonException or InvalidOperationException)
            {
                failed++;
                logger.LogDebug(ex, "Holded purchase document {DocId} could not be read for its notes", doc.HoldedDocId);
            }
        }
        if (failed > 0)
            logger.LogWarning(
                "Holded document sync: notes unreadable for {Failed} purchase document(s); the next sync retries them.",
                failed);
    }

    private static HoldedExpenseDoc MapDoc(
        HoldedPurchaseDocListItemDto doc,
        HoldedMatchEntry[] entries,
        IReadOnlySet<string> managedIds,
        Instant now)
    {
        // The whole doc goes on its FIRST line's account, with the union of doc and line tags: real purchase docs are
        // single-line today.
        var bookedAccount = doc.Lines.Count > 0 ? doc.Lines[0].AccountId : null;
        var tags = doc.Tags
            .Concat(doc.Lines.SelectMany(l => l.Tags))
            .ToList();

        var matchResult = HoldedMatcher.Match(bookedAccount, tags, entries, managedIds);

        var localDate = doc.Date.InZone(MadridZone).Date;

        return new HoldedExpenseDoc
        {
            Id = Guid.NewGuid(),
            HoldedDocId = doc.Id,
            DocNumber = doc.DocNumber,
            ContactName = doc.ContactName,
            ContactId = string.IsNullOrEmpty(doc.ContactId) ? null : doc.ContactId,
            Description = string.IsNullOrWhiteSpace(doc.Description) ? null : doc.Description.Trim(),
            Date = localDate,
            Subtotal = doc.Subtotal,
            Tax = doc.Tax,
            Total = doc.Total,
            Currency = doc.Currency,
            // Strict equality: an absent `draft` is NOT approved. A wrongly approved doc leaks into the actuals; a
            // wrongly unapproved one just stays on the Unmatched queue.
            IsApproved = doc.IsDraft == false,
            TagsJson = JsonSerializer.Serialize(tags),
            BookedAccountId = bookedAccount,
            BudgetCategoryId = matchResult.CategoryId,
            MatchStatus = matchResult.CategoryId is null && !matchResult.IsManaged
                ? HoldedMatchStatus.Unmatched
                : HoldedMatchStatus.Matched,
            MatchSource = matchResult.Source,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

}
