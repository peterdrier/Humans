using Humans.Holded.Contracts;
using Humans.Holded.Data;
using NodaTime;
using static Humans.Holded.Services.HoldedJson;

namespace Humans.Holded.Services;

/// <summary>The ensure-style write face. Holded ids are looked up live (contacts, documents) or from the chart mirror
/// (account ids) inside each call and never leave it.</summary>
internal sealed class HoldedWrite(
    IHoldedClient client,
    IHoldedMirrorRepository repo,
    IHoldedService holded,
    IClock clock,
    ILogger<HoldedWrite> logger) : IHoldedWrite
{
    private const string CreditorType = "creditor";
    private const int CreditorRangeFrom = 40000000;
    private const int CreditorRangeTo = 41999999;

    public async Task<HoldedCreditorContactOutcome> EnsureCreditorContactAsync(
        string legalName, string? tradeName, string? iban, CancellationToken ct = default)
    {
        // Any type: contacts made by hand in Holded's UI are often "supplier", and a twin is the failure to avoid.
        var existing = (await client.ListContactsAsync(ct)).FirstOrDefault(c =>
            c.SupplierAccountNum is >= CreditorRangeFrom and <= CreditorRangeTo
            && string.Equals(c.Name, legalName, StringComparison.Ordinal));
        if (existing is not null)
            return new HoldedCreditorContactOutcome(RequireAccountNum(existing), Created: false);

        var id = await client.CreateContactAsync(new HoldedContactInput
        {
            Name = legalName,
            TradeName = tradeName,
            Type = CreditorType,
            Iban = iban,
        }, ct);
        // The POST answers with the id only; the account number Holded minted is on the contact.
        var created = await client.GetContactAsync(id, ct);
        return new HoldedCreditorContactOutcome(RequireAccountNum(created), Created: true);
    }

    public async Task<HoldedPurchaseDocumentOutcome> EnsurePurchaseDocumentAsync(
        int creditorAccountNum, int expenseAccountNum, string description, decimal amount, LocalDate date,
        string notes, HoldedAttachmentInput? attachment, bool attachmentUploaded, CancellationToken ct = default)
    {
        var contact = await FindContactByAccountAsync(creditorAccountNum, ct)
            ?? throw new HoldedPermanentException(
                $"No Holded contact owns creditor account {creditorAccountNum}.");

        var found = await FindDocumentAsync(contact.Id, description, amount, date: null, ct);
        var match = found.FirstOrDefault(d => string.Equals(d.Doc.Notes, notes, StringComparison.Ordinal));

        var created = match.Doc is null;
        var docId = match.Doc?.Id;
        if (created)
        {
            var account = (await repo.GetAccountsAsync(ct)).FirstOrDefault(a => a.Number == expenseAccountNum)
                ?? throw new HoldedPermanentException(
                    $"Expense account {expenseAccountNum} is not in the chart mirror; ensure it first.");
            docId = await client.CreatePurchaseDocumentAsync(new HoldedPurchaseDocumentInput
            {
                ContactId = contact.Id,
                ContactName = contact.Name ?? "",
                Date = date.AtStartOfDayInZone(MadridZone).ToInstant(),
                Description = description,
                Notes = notes,
                Lines = [new HoldedPurchaseDocumentLineInput
                {
                    Description = description, Amount = amount, AccountId = account.HoldedId,
                }],
            }, ct);
        }

        var attached = false;
        if (attachment is not null && (created || !attachmentUploaded))
        {
            await client.UploadAttachmentAsync(docId!, attachment, ct);
            attached = true;
        }

        var doc = created ? await client.GetPurchaseDocumentAsync(docId!, ct) : match.Doc!;
        var approved = false;
        if (doc.ApprovedAt is null)
        {
            await client.ApprovePurchaseDocumentAsync(docId!, ct);
            approved = true;
        }

        return new HoldedPurchaseDocumentOutcome(created, attached, approved, doc.DocNumber);
    }

    public async Task<bool> EnsureExpenseAccountAsync(int number, string name, CancellationToken ct = default)
    {
        if ((await repo.GetAccountsAsync(ct)).Any(a => a.Number == number))
            return false;

        // The mirror is as old as the last sync; an account made in Holded's UI since then must not get a twin.
        var exists = (await client.ListExpenseAccountsAsync(ct)).Any(a => a.AccountNum == number);
        if (!exists)
            await client.CreateExpenseAccountAsync(number, name, ct);
        var now = clock.GetCurrentInstant();
        var chart = await client.ListAccountingAccountsAsync(ct);
        await repo.UpsertAccountsAsync(chart.Select(a => Service.ToAccountEntity(a, now)).ToList(), now, ct);
        return !exists;
    }

    public async Task<HoldedDeleteOutcome> DeletePurchaseDocumentAsync(
        int creditorAccountNum, string description, decimal amount, LocalDate date, string expectedNotes,
        CancellationToken ct = default)
    {
        var contact = await FindContactByAccountAsync(creditorAccountNum, ct);
        if (contact is null) return HoldedDeleteOutcome.NotFound;

        var found = await FindDocumentAsync(contact.Id, description, amount, date, ct);
        if (found.Count == 0) return HoldedDeleteOutcome.NotFound;

        var (_, doc) = found.FirstOrDefault(d => string.Equals(d.Doc.Notes, expectedNotes, StringComparison.Ordinal));
        if (doc is null)
        {
            logger.LogWarning(
                "Refused to delete Holded purchase on {Account} ({Description}, {Amount}): notes differ",
                creditorAccountNum, description, amount);
            return HoldedDeleteOutcome.RefusedNotesDiffer;
        }
        if (doc.PaymentsTotal != 0m || doc.Payments.Count > 0)
            return HoldedDeleteOutcome.RefusedPaid;

        await client.DeletePurchaseDocumentAsync(doc.Id, ct);
        // The document's ledger lines are gone at Holded; the trailing sweep drops them from the mirror.
        await holded.SyncLedgerAsync(full: false, CancellationToken.None);
        return HoldedDeleteOutcome.Deleted;
    }

    public async Task<HoldedJournalEntryOutcome> PostJournalEntryAsync(
        LocalDate date, int debitAccount, int creditAccount, decimal amount, string description,
        CancellationToken ct = default)
    {
        var id = await client.PostLedgerEntryAsync(date, debitAccount, creditAccount, amount, description, ct);
        return new HoldedJournalEntryOutcome(!id.StartsWith(UnconfirmedPaymentRefPrefix, StringComparison.Ordinal));
    }

    private async Task<HoldedContactDto?> FindContactByAccountAsync(int accountNum, CancellationToken ct) =>
        (await client.ListContactsAsync(ct)).FirstOrDefault(c => c.SupplierAccountNum == accountNum);

    /// <summary>The list carries no notes, so each candidate on contact, description, total (and date when given)
    /// is read in full.</summary>
    private async Task<List<(string Id, HoldedPurchaseDocumentDto Doc)>> FindDocumentAsync(
        string contactId, string description, decimal amount, LocalDate? date, CancellationToken ct)
    {
        var candidates = (await client.ListPurchaseDocumentsAsync(ct))
            .Where(d => string.Equals(d.ContactId, contactId, StringComparison.Ordinal)
                && string.Equals(d.Description, description, StringComparison.Ordinal)
                && d.Total == amount
                && (date is null || d.Date.InZone(MadridZone).Date == date))
            .ToList();
        var docs = new List<(string, HoldedPurchaseDocumentDto)>();
        foreach (var c in candidates)
            docs.Add((c.Id, await client.GetPurchaseDocumentAsync(c.Id, ct)));
        return docs;
    }

    private static int RequireAccountNum(HoldedContactDto contact) =>
        contact.SupplierAccountNum ?? throw new HoldedPermanentException(
            $"Holded contact {contact.Id} has no ledger account number yet.");
}
