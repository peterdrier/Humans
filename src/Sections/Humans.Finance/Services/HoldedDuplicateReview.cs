using System.Globalization;
using Humans.AuditLog.Contracts;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Expenses.Contracts;
using Humans.Finance.Contracts;
using Humans.Finance.Data;
using Humans.Finance.Models;
using Humans.Holded.Contracts;
using static Humans.Finance.Services.HoldedCreditorBindings;
using static Humans.Finance.Services.HoldedCreditorReader;
using static Humans.Finance.Services.SepaText;

namespace Humans.Finance.Services;

internal sealed class HoldedDuplicateReview(
    IHoldedRepository repo,
    IHoldedService holded,
    IHoldedWrite holdedWrite,
    IExpenseHoldedBookingService expenses,
    HoldedCreditorReader creditorReader,
    IAuditLogService audit,
    ILogger<Service> logger) : IApplicationService
{
    internal const string PurchaseMirrorUnavailable =
        "The Holded purchase-document mirror has never synced, so no duplicate can be judged. Run Sync now on /Holded.";

    /// <summary>The kept expense line: its id is the one Guid in the story; Holded ids go in the text.</summary>
    private const string ExpenseLineEntityType = "ExpenseLine";

    internal Task<HoldedDuplicatesVm> GetHoldedDuplicatesAsync(CancellationToken ct = default) =>
        BuildDuplicatePlanAsync(ct);

    /// <summary>
    /// From the mirrors, never a stored plan. The claim is re-run on each candidate's mirrored notes, so a candidate
    /// goes to the line whose notes it carries and only an exact match can be deleted. One whose notes or payments the
    /// mirror does not know yet is refused.
    /// </summary>
    private async Task<HoldedDuplicatesVm> BuildDuplicatePlanAsync(CancellationToken ct)
    {
        var mirror = await holded.GetPurchaseDocumentsAsync(ct);
        if (mirror.LastSyncAt is null)
            return new HoldedDuplicatesVm([], [], PurchaseMirrorUnavailable);
        var docs = mirror.Docs.Select(ToListItem).ToList();
        var notesById = mirror.Docs.ToDictionary(d => d.Id, d => d.Notes, StringComparer.Ordinal);
        var paymentsUnknown = mirror.Docs.Where(d => d.PaymentsPending is null).Select(d => d.Id)
            .ToHashSet(StringComparer.Ordinal);
        string? Refusal(HoldedPurchaseDocListItemDto d) => paymentsUnknown.Contains(d.Id)
            ? "its payments are not mirrored yet — sync Holded and look again"
            : DeleteRefusal(d);
        // Without it the same-named sibling contacts go unseen, so the plan would look complete while missing them.
        if (await creditorReader.ContactsByIdAsync(ct) is not { } contactById)
            return new HoldedDuplicatesVm([], [], ContactListUnavailable);
        var bindings = await repo.GetCreditorContactsAsync(ct);
        var bookings = await expenses.GetHoldedBookingsAsync(ct);
        var contactsByUser = MemberContacts(bookings, bindings, contactById);
        var bindingByUser = bindings.ToDictionary(b => b.UserId);
        var docsByContact = docs.Where(d => !string.IsNullOrEmpty(d.ContactId))
            .ToLookup(d => d.ContactId!, StringComparer.Ordinal);

        var candidates = HoldedDuplicateMatcher.Find(bookings, docs, contactsByUser);
        var verified = HoldedDuplicateMatcher.Find(bookings, docs, contactsByUser,
            (b, d) => HoldedDuplicateMatcher.Matches(b, d)
                      && Refusal(d) is null
                      && notesById.GetValueOrDefault(d.Id) is { } notes
                      && string.Equals(notes, b.Notes, StringComparison.Ordinal));
        var deletableIds = verified.SelectMany(m => m.Duplicates.Select(d => d.Id)).ToHashSet(StringComparer.Ordinal);
        var byRef = (IEqualityComparer<ExpenseHoldedBooking>)ReferenceEqualityComparer.Instance;
        var candidateByBooking = candidates.ToDictionary(m => m.Booking, byRef);
        var verifiedByBooking = verified.ToDictionary(m => m.Booking, byRef);

        var matched = new List<(ExpenseHoldedBooking Booking, HoldedPurchaseDocListItemDto Kept, HoldedDuplicateLine Line)>();
        foreach (var linked in bookings)
        {
            var named = candidateByBooking.GetValueOrDefault(linked);
            var ok = verifiedByBooking.GetValueOrDefault(linked);
            if (named is null && ok is null) continue;
            var deletable = ok?.Duplicates.Select(d => d.Id).ToList() ?? [];
            var refused = (named?.Duplicates ?? [])
                .Where(d => !deletableIds.Contains(d.Id))
                .Select(d => new HoldedDuplicateRefusal(d.Id, d.Total, Refusal(d)
                    ?? (notesById.GetValueOrDefault(d.Id) is null
                        ? "its notes are not mirrored yet — the next sync reads them"
                        : "notes do not match the push — judge it by hand")))
                .ToList();
            if (deletable.Count == 0 && refused.Count == 0) continue;
            var kept = (ok ?? named)!.Kept;
            matched.Add((linked, kept, new HoldedDuplicateLine(
                linked.ExpenseReportId, linked.LineId, linked.Description,
                linked.BookedAmount, kept.Id, deletable, refused)));
        }

        var members = matched
            .GroupBy(l => l.Booking.CreditorUserId)
            .Select(g =>
            {
                var binding = bindingByUser.GetValueOrDefault(g.Key);
                var lines = g.Select(l => l.Line).ToList();

                // A contact the race created beside the bound one holds only duplicates, so it goes once they do.
                var deletableIds = lines.SelectMany(l => l.DeletableDocIds).ToHashSet(StringComparer.Ordinal);
                var orphans = contactsByUser[g.Key]
                    .Where(c => !string.Equals(c, binding?.HoldedContactId, StringComparison.Ordinal))
                    .Where(c => docsByContact[c].Any() && docsByContact[c].All(d => deletableIds.Contains(d.Id)))
                    .Order(StringComparer.Ordinal)
                    .ToList();

                var contact = (binding?.HoldedContactId is { Length: > 0 } boundId ? contactById.GetValueOrDefault(boundId) : null)
                              ?? contactById.GetValueOrDefault(g.First().Kept.ContactId ?? "");
                return new HoldedDuplicateMember(
                    g.Key,
                    contact?.Name ?? g.First().Kept.ContactName,
                    binding?.HoldedContactId,
                    contact?.SupplierAccountNum,
                    lines, orphans);
            })
            .ToList();

        // Holded has no archive call, so an empty unbound contact is listed, not touched.
        var boundContacts = bindings.Select(b => b.HoldedContactId).ToHashSet(StringComparer.Ordinal);
        var empty = contactById.Values
            .Where(c => IsMemberCreditorAccount(c.SupplierAccountNum)
                        && !boundContacts.Contains(c.Id) && !docsByContact[c.Id].Any())
            .Select(c => (AccountNum: c.SupplierAccountNum!.Value, Summary: $"{c.SupplierAccountNum} {c.Name} ({c.Id})"))
            .ToList();

        return new HoldedDuplicatesVm(members, empty, null);
    }

    /// <summary>Holded refuses to delete a document carrying a payment (a reconciled bank line counts), and a draft
    /// reports no <c>payments_pending</c>, so it reads as paid too.</summary>
    private static string? DeleteRefusal(HoldedPurchaseDocListItemDto d)
    {
        if (d.PaymentsPending >= d.Total) return null;
        return d.IsDraft == true
            ? "still a draft in Holded — delete it by hand"
            : $"has a payment on it ({Euros(d.Total - d.PaymentsPending)} of {Euros(d.Total)} paid) — settle it in Holded by hand";
    }

    internal async Task<HoldedDuplicateApplyResult> RemoveHoldedDuplicatesAsync(Guid actorUserId)
    {
        // A deletion that has started finishes.
        var ct = CancellationToken.None;

        HoldedDuplicatesVm plan;
        try
        {
            // Deletion judges Holded as it is now, not as the nightly sync left it.
            if (!await holded.SyncPurchaseDocumentsAsync(ct) || !await holded.SyncContactsAsync(ct))
                return new HoldedDuplicateApplyResult(
                    [], "A Holded sync is already running — nothing was deleted; try again in a moment.", null);
            plan = await BuildDuplicatePlanAsync(ct);
            // The list sync keeps notes it already holds, and an accountant may have edited them since: re-read the
            // notes of every document about to be deleted, then judge again.
            var deletable = plan.Members.SelectMany(m => m.Lines).SelectMany(l => l.DeletableDocIds).ToList();
            if (deletable.Count > 0)
            {
                await holded.SyncPurchaseDocumentNotesAsync(deletable, ct);
                plan = await BuildDuplicatePlanAsync(ct);
            }
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogWarning("Holded could not be read; no duplicate was deleted. {Reason}", ex.Message);
            return new HoldedDuplicateApplyResult([], "Holded could not be read — nothing was deleted: " + ex.Message, null);
        }
        if (plan.Error is { } planError)
            return new HoldedDuplicateApplyResult([], "Nothing was deleted: " + planError, null);
        if (!plan.HasWork)
            return new HoldedDuplicateApplyResult([], "No duplicate document was found — nothing was deleted.", null);

        var before = await ReadCreditorBalancesAsync(ct);
        var results = new List<HoldedDuplicateMemberResult>();
        foreach (var member in plan.Members.OrderBy(m => m.SupplierAccountNum ?? int.MaxValue)
                     .ThenBy(m => m.ContactName, StringComparer.OrdinalIgnoreCase))
        {
            var account = member.SupplierAccountNum?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
            var deletedDocs = new List<string>();
            var deletedContacts = new List<string>();
            var failures = new List<string>();
            foreach (var line in member.Lines)
                foreach (var docId in line.DeletableDocIds)
                {
                    try
                    {
                        await holdedWrite.DeletePurchaseDocumentByIdAsync(docId, ct);
                        deletedDocs.Add(docId);
                        await audit.LogAsync(
                            FinanceAuditActions.HoldedDuplicateRemoved, ExpenseLineEntityType, line.LineId,
                            $"DELETED duplicate Holded purchase document {docId} ({Euros(line.Amount)}, "
                            + $"'{line.Description}') of {member.ContactName} on creditor account {account}; "
                            + $"kept {line.KeptDocId}.",
                            actorUserId, member.UserId, AuditSubjectTypes.User);
                    }
                    catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
                    {
                        logger.LogError(ex, "Holded refused to delete duplicate purchase document {DocId}.", docId);
                        failures.Add($"document {docId}: {ex.Message}");
                    }
                }

            // A refused document keeps its contact.
            if (failures.Count == 0)
                foreach (var contactId in member.OrphanContactIds)
                {
                    try
                    {
                        await holdedWrite.DeleteContactAsync(contactId, ct);
                        deletedContacts.Add(contactId);
                        await audit.LogAsync(
                            FinanceAuditActions.HoldedDuplicateRemoved, AuditSubjectTypes.User, member.UserId,
                            $"DELETED duplicate Holded contact {contactId} of {member.ContactName}; kept "
                            + $"{member.BoundContactId ?? "the bound contact"}.",
                            actorUserId);
                    }
                    catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
                    {
                        logger.LogError(ex, "Holded refused to delete duplicate contact {ContactId}.", contactId);
                        failures.Add($"contact {contactId}: {ex.Message}");
                    }
                }

            results.Add(new HoldedDuplicateMemberResult(
                member.UserId, member.ContactName, member.SupplierAccountNum,
                member.SupplierAccountNum is { } num ? before?.GetValueOrDefault(num) : null, null,
                deletedDocs, deletedContacts, failures));
        }

        string? syncError = null;
        try
        {
            await holded.SyncPurchaseDocumentsAsync(ct);
            await holded.SyncLedgerAsync(ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The Holded sync after removing duplicates failed; the deletions stand.");
            syncError = ex.Message;
        }

        var after = await ReadCreditorBalancesAsync(ct);
        return new HoldedDuplicateApplyResult(
            results.Select(r => r with
            {
                BalanceAfter = r.SupplierAccountNum is { } num ? after?.GetValueOrDefault(num) : null,
            }).ToList(),
            null, syncError);
    }

    /// <summary>Holded's own sign. Null when unreadable, so the result shows no figure rather than a stale one.</summary>
    private async Task<Dictionary<int, decimal>?> ReadCreditorBalancesAsync(CancellationToken ct)
    {
        try
        {
            await holded.SyncChartAsync(ct);
            return (await holded.GetChartAsync(ct))
                .Where(a => IsMemberCreditorAccount(a.Number))
                .GroupBy(a => a.Number)
                .ToDictionary(g => g.Key, g => g.First().HoldedBalance);
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogWarning("Holded's chart of accounts could not be read for the duplicate removal balances. {Reason}", ex.Message);
            return null;
        }
    }

}
