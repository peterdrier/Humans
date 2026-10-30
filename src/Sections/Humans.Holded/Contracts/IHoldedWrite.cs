using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Holded.Contracts;

/// <summary>
/// Ensure-style writes to Holded. Each call takes Humans keys (account numbers, names, the text Humans wrote),
/// finds the Holded ids it needs live inside the call, is safe to repeat, and returns an outcome, never a Holded id.
/// Every method writes to Holded, so callers never hand it a request-scoped cancellation token.
/// </summary>
public interface IHoldedWrite : IApplicationService
{
    /// <summary>The creditor contact's ledger account (<c>supplier_record.num</c>, 41xxxxxx for a creditor). Found
    /// by <paramref name="customId"/>, else by exact legal name among <c>creditor</c> contacts, else created. Never
    /// updates a contact: v2's PUT is a full replacement that would mint a second creditor account. Throws
    /// <see cref="HoldedPermanentException"/> when Holded has not assigned the contact an account number.</summary>
    [ExternalWrite]
    Task<HoldedCreditorContactOutcome> EnsureCreditorContactAsync(
        string legalName, string? tradeName, string? iban, string customId, CancellationToken ct = default);

    /// <summary>
    /// A purchase document on the creditor's contact booking <paramref name="amount"/> to
    /// <paramref name="expenseAccountNum"/>. An existing document matches on contact, description, total and notes;
    /// otherwise one is created. The attachment is uploaded when the document is new, or when
    /// <paramref name="attachmentUploaded"/> says an earlier call did not get that far: Holded's v2 purchase read
    /// does not report attachments, so the caller remembers. A draft is approved.
    /// Throws <see cref="HoldedPermanentException"/> when no contact owns the creditor account or the expense account
    /// is not in the chart mirror.
    /// </summary>
    [ExternalWrite]
    Task<HoldedPurchaseDocumentOutcome> EnsurePurchaseDocumentAsync(
        int creditorAccountNum, int expenseAccountNum, string description, decimal amount, LocalDate date,
        string notes, HoldedAttachmentInput? attachment, bool attachmentUploaded, CancellationToken ct = default);

    /// <summary>The expense account, created through <c>expenses-accounts</c> when the chart mirror lacks the number;
    /// the chart mirror is refreshed after a create. Returns true when it was created.</summary>
    [ExternalWrite]
    Task<bool> EnsureExpenseAccountAsync(int number, string name, CancellationToken ct = default);

    /// <summary>Deletes the matching purchase document (contact, description, total and date) and re-syncs the ledger
    /// window. Refused, untouched, when its notes are not <paramref name="expectedNotes"/> (someone else's document)
    /// or it carries a payment.</summary>
    [ExternalWrite]
    Task<HoldedDeleteOutcome> DeletePurchaseDocumentAsync(
        int creditorAccountNum, string description, decimal amount, LocalDate date, string expectedNotes,
        CancellationToken ct = default);

    /// <summary>Debit one account, credit the other, one amount. Accounts are ledger numbers. Not idempotent: Holded
    /// has no key to find an earlier post by, so callers record the outcome and never repeat a posted entry.
    /// <see cref="HoldedJournalEntryOutcome.Confirmed"/> is false when Holded accepted the entry without a readable
    /// id; it is posted either way.</summary>
    [ExternalWrite]
    Task<HoldedJournalEntryOutcome> PostJournalEntryAsync(
        LocalDate date, int debitAccount, int creditAccount, decimal amount, string description,
        CancellationToken ct = default);
}

/// <summary><paramref name="AccountNum"/> is the contact's ledger account; <paramref name="Created"/> is true when
/// this call created the contact.</summary>
public sealed record HoldedCreditorContactOutcome(int AccountNum, bool Created);

/// <summary>What the call did. <paramref name="DocNumber"/> is Holded's human document number, for display.</summary>
public sealed record HoldedPurchaseDocumentOutcome(bool Created, bool Attached, bool Approved, string DocNumber);

public enum HoldedDeleteOutcome
{
    Deleted,
    /// <summary>No live document matched: already gone, or never created.</summary>
    NotFound,
    /// <summary>A document matched but its notes differ: not this caller's to delete.</summary>
    RefusedNotesDiffer,
    /// <summary>The document carries a payment; Holded refuses those and so does this.</summary>
    RefusedPaid,
}

public sealed record HoldedJournalEntryOutcome(bool Confirmed);
