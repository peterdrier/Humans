using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Holded.Contracts;

/// <summary>
/// The only way another section writes to Holded; nothing outside the Holded section calls Holded's API. The ensure
/// members take Humans keys (account numbers, names, the text Humans wrote), find the Holded ids they need live inside
/// the call, are safe to repeat, and return an outcome. The id-keyed members serve callers that already store Holded's
/// ids (creditor bindings, expense lines, store orders, SEPA payment refs) and do exactly one step each. Writes never take a
/// request-scoped cancellation token.
/// </summary>
public interface IHoldedWrite : IApplicationService
{
    /// <summary>The creditor contact's ledger account (<c>supplier_record.num</c>, 41xxxxxx for a creditor). Found
    /// by exact legal name among contacts with an account in 40000000–41999999 (any contact type: hand-made ones are
    /// often <c>supplier</c>), else created. No <c>custom_id</c> lookup: Humans has never sent one, and whether v2's
    /// create stores it needs a live write to confirm. Never
    /// updates a contact: v2's PUT is a full replacement that would mint a second creditor account. Throws
    /// <see cref="HoldedPermanentException"/> when Holded has not assigned the contact an account number.</summary>
    [ExternalWrite]
    Task<HoldedCreditorContactOutcome> EnsureCreditorContactAsync(
        string legalName, string? tradeName, string? iban, CancellationToken ct = default);

    /// <summary>The expense account, created through <c>expenses-accounts</c> when neither the chart mirror nor Holded's
    /// live expense-account list has the number; the chart mirror is refreshed whenever the mirror lacked it.
    /// <paramref name="name"/> is used only to create.</summary>
    [ExternalWrite]
    Task<HoldedExpenseAccountOutcome> EnsureExpenseAccountAsync(int number, string name, CancellationToken ct = default);

    /// <summary>Creates a contact and refreshes the contact mirror so it carries it. Returns Holded's contact id. Never
    /// looks for an existing one: callers that must not mint a twin decide that first.</summary>
    [ExternalWrite]
    Task<string> CreateContactAsync(HoldedContactInput input, CancellationToken ct = default);

    /// <summary>Deletes a contact by id and refreshes the contact mirror.</summary>
    [ExternalWrite]
    Task DeleteContactAsync(string contactId, CancellationToken ct = default);

    /// <summary>Creates a draft purchase document; returns its id. Follow with
    /// <see cref="EnsurePurchaseDocumentApprovedAsync"/>.</summary>
    [ExternalWrite]
    Task<string> CreatePurchaseDocumentAsync(HoldedPurchaseDocumentInput input, CancellationToken ct = default);

    /// <summary>Uploads one attachment to a purchase document. Not idempotent: Holded's read reports no attachments,
    /// so callers record the upload.</summary>
    [ExternalWrite]
    Task UploadPurchaseAttachmentAsync(
        string documentId, HoldedAttachmentInput attachment, CancellationToken ct = default);

    /// <summary>Approves the purchase document unless Holded already has it approved.</summary>
    [ExternalWrite]
    Task EnsurePurchaseDocumentApprovedAsync(string documentId, CancellationToken ct = default);

    /// <summary>Deletes a purchase document by id. Holded refuses one that carries a payment; callers check first.
    /// The ledger mirror drops its lines on the next sweep.</summary>
    [ExternalWrite]
    Task DeletePurchaseDocumentByIdAsync(string documentId, CancellationToken ct = default);

    /// <summary>Every sales document of either kind carrying <paramref name="tag"/>, read live: the guard a caller runs
    /// before issuing so a retried issuance adopts the earlier document instead of creating a second.</summary>
    Task<IReadOnlyList<(HoldedSalesDocumentKind Kind, HoldedSalesDocumentDto Document)>> FindSalesDocumentsByTagAsync(
        string tag, CancellationToken ct = default);

    /// <summary>Creates the sales document, approves it, and returns it as Holded now holds it. Each line's
    /// <see cref="HoldedSalesDocumentLineInput.AccountNum"/> is resolved to Holded's account through the chart mirror;
    /// throws <see cref="HoldedPermanentException"/> for a number the mirror lacks.</summary>
    [ExternalWrite]
    Task<HoldedSalesDocumentDto> IssueSalesDocumentAsync(
        HoldedSalesDocumentKind kind, HoldedSalesDocumentInput input, CancellationToken ct = default);

    /// <summary>Approves a draft sales document and returns it as Holded now holds it.</summary>
    [ExternalWrite]
    Task<HoldedSalesDocumentDto> ApproveSalesDocumentAsync(
        HoldedSalesDocumentKind kind, string documentId, CancellationToken ct = default);

    /// <summary>Deletes the matching purchase document (contact, description, total and date) and re-syncs the ledger
    /// window. Refused, untouched, when its notes are not <paramref name="expectedNotes"/> (someone else's document)
    /// or it carries a payment. The re-sync is the trailing window: a document dated before it stays in the mirror
    /// until the nightly balance check re-pulls the account.</summary>
    [ExternalWrite]
    Task<HoldedDeleteOutcome> DeletePurchaseDocumentAsync(
        int creditorAccountNum, string description, decimal amount, LocalDate date, string expectedNotes,
        CancellationToken ct = default);

    /// <summary>Debit one account, credit the other, one amount. Accounts are ledger numbers. Not idempotent: Holded
    /// has no key to find an earlier post by, so callers record the outcome and never repeat a posted entry.
    /// <see cref="HoldedJournalEntryOutcome.Confirmed"/> is false when Holded accepted the entry without a readable
    /// id; it is posted either way.</summary>
    /// <summary>Pays <paramref name="amount"/> of one purchase document from <paramref name="treasuryId"/> (Holded's
    /// default when blank), dated <paramref name="date"/>. Not idempotent. Returns the payment's ref, the one Holded id
    /// this face hands out: a bank-line reconcile has to name it. An <c>unconfirmed:{documentId}</c> ref means Holded
    /// took the payment but its id could not be read; it is posted either way. Throws only when nothing was posted,
    /// including after a 5xx, which is checked against the document.</summary>
    [ExternalWrite]
    Task<string> PayPurchaseDocumentAsync(
        string documentId, decimal amount, string? treasuryId, LocalDate date, string? description,
        CancellationToken ct = default);

    /// <summary>Reconciles one bank line against payment refs (<see cref="HoldedReconcileDocumentType"/>). Throws
    /// <see cref="HoldedPermanentException"/> when Holded refuses the pairing; a refused reconcile moved no money.</summary>
    [ExternalWrite]
    Task ReconcileBankMovementAsync(
        string treasuryAccountId, string movementId, IReadOnlyList<HoldedReconcileDocumentRef> documents,
        CancellationToken ct = default);

    [ExternalWrite]
    Task<HoldedJournalEntryOutcome> PostJournalEntryAsync(
        LocalDate date, int debitAccount, int creditAccount, decimal amount, string description,
        CancellationToken ct = default);
}

/// <summary><paramref name="AccountNum"/> is the contact's ledger account; <paramref name="Created"/> is true when
/// this call created the contact.</summary>
public sealed record HoldedCreditorContactOutcome(int AccountNum, bool Created);

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

/// <summary><paramref name="HoldedId"/> is the account's Holded id, what a document line books to;
/// <paramref name="Created"/> is true when this call created the account.</summary>
public sealed record HoldedExpenseAccountOutcome(string HoldedId, bool Created);
