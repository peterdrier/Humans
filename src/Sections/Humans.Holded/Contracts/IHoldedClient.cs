using NodaTime;

namespace Humans.Holded.Contracts;

public interface IHoldedClient
{
    /// <summary>
    /// False without <c>HOLDED_API_KEY_V2</c> (PR previews, local dev). Every call would 401, so callers skip the
    /// work instead of writing off their queues.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>Returns the new doc id.</summary>
    Task<string> CreatePurchaseDocumentAsync(
        HoldedPurchaseDocumentInput input,
        CancellationToken ct = default);

    Task UploadAttachmentAsync(
        string documentId,
        HoldedAttachmentInput attachment,
        CancellationToken ct = default);

    Task<HoldedPurchaseDocumentDto> GetPurchaseDocumentAsync(
        string documentId,
        CancellationToken ct = default);

    /// <summary>POST /purchases only creates a draft; only an approved doc books to the ledger. Idempotent: an
    /// already approved doc returns normally.</summary>
    Task ApprovePurchaseDocumentAsync(string documentId, CancellationToken ct = default);

    /// <summary>Holded refuses a document that carries a payment, so callers check <c>payments_pending</c> first and
    /// never retry a 4xx.</summary>
    Task DeletePurchaseDocumentAsync(string documentId, CancellationToken ct = default);

    /// <summary>Partial payments are allowed. Omitting <paramref name="treasuryId"/> lets Holded pick its default.
    /// The POST returns no id, so the id is the one new <c>payments_detail</c> entry matching amount, date and
    /// treasury, read before and after. A failed before-read throws (nothing was posted). Once posted it never
    /// throws: when the after-read fails or zero or several entries match it returns
    /// <c>"unconfirmed:{documentId}"</c>, which callers persist like any ref.</summary>
    Task<string> PayPurchaseDocumentAsync(
        string documentId, decimal amount, string? treasuryId, LocalDate date, string? description,
        CancellationToken ct = default);

    /// <summary>Debit one account, credit the other, one amount; returns the entry id. Accounts are ledger
    /// <em>numbers</em> (<c>57200001</c>), not Holded ids. An unreadable success returns <c>"unconfirmed:entry"</c>
    /// rather than throwing, as in <see cref="PayPurchaseDocumentAsync"/>.</summary>
    Task<string> PostLedgerEntryAsync(
        LocalDate date, int debitAccount, int creditAccount, decimal amount, string description,
        CancellationToken ct = default);

    Task<IReadOnlyList<HoldedExpenseAccountDto>> ListExpenseAccountsAsync(
        CancellationToken ct = default);

    /// <summary>Returns the new account id.</summary>
    Task<string> CreateExpenseAccountAsync(
        int accountNum, string name, CancellationToken ct = default);

    Task<IReadOnlyList<HoldedPurchaseDocListItemDto>> ListPurchaseDocumentsAsync(
        CancellationToken ct = default);

    /// <summary>Creates only, never updates: v2's contact PUT is a full replacement that would reset the
    /// contact's creditor account.</summary>
    Task<string> UpsertContactAsync(HoldedContactInput input, CancellationToken ct = default);

    /// <summary>A draft books nothing: follow with <see cref="ApproveSalesDocumentAsync"/>.</summary>
    Task<string> CreateSalesDocumentAsync(
        HoldedSalesDocumentKind kind, HoldedSalesDocumentInput input, CancellationToken ct = default);

    /// <summary>Only an approved doc books revenue and gets a number.</summary>
    Task ApproveSalesDocumentAsync(
        HoldedSalesDocumentKind kind, string documentId, CancellationToken ct = default);

    /// <summary>v2 has no server-side tag filter, so the collection is walked and matched client-side. Finds a
    /// document an earlier attempt already issued.</summary>
    Task<IReadOnlyList<string>> FindSalesDocumentIdsByTagAsync(
        HoldedSalesDocumentKind kind, string tag, CancellationToken ct = default);

    Task<HoldedSalesDocumentDto> GetSalesDocumentAsync(
        HoldedSalesDocumentKind kind, string documentId, CancellationToken ct = default);

    Task<HoldedContactDto> GetContactAsync(string contactId, CancellationToken ct = default);

    /// <summary>A contact already gone (404) is success. Holded keeps a contact's documents, so deleting one that
    /// owns any is the caller's bug.</summary>
    Task DeleteContactAsync(string contactId, CancellationToken ct = default);

    Task<IReadOnlyList<HoldedLedgerLineDto>> ListLedgerEntriesAsync(
        LocalDate from, LocalDate to, int? accountNum = null, CancellationToken ct = default);

    Task<IReadOnlyList<HoldedAccountDto>> ListAccountingAccountsAsync(CancellationToken ct = default);

    Task<HoldedUsageDto> GetUsageAsync(CancellationToken ct = default);

    /// <summary>A page that can't be completed throws rather than returning the prefix. One contact with an
    /// unreadable value is skipped and logged; an unreadable page envelope is not.</summary>
    Task<IReadOnlyList<HoldedContactDto>> ListContactsAsync(CancellationToken ct = default);

    /// <summary>Order not guaranteed. The window is also applied client-side, whether or not Holded honours the
    /// date parameters. A pending outgoing line with no readable date fails the read closed
    /// (<c>HoldedPermanentException.BankMovementId</c> names it) unless its id is in
    /// <paramref name="skipUnreadableDateIds"/>, in which case it is skipped and logged.</summary>
    Task<IReadOnlyList<HoldedBankMovementDto>> ListBankMovementsAsync(
        string treasuryAccountId, LocalDate from, LocalDate to, CancellationToken ct = default,
        IReadOnlySet<string>? skipUnreadableDateIds = null);

    /// <summary>Several documents on one line are allowed. Throws <c>HoldedPermanentException</c> when Holded
    /// refuses the pairing: unlike a payment, a refused reconcile moved no money.</summary>
    Task ReconcileBankMovementAsync(
        string treasuryAccountId, string movementId,
        IReadOnlyList<HoldedReconcileDocumentRef> documents, CancellationToken ct = default);
}
