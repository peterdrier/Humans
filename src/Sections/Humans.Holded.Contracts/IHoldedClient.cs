using NodaTime;

namespace Humans.Holded.Contracts;

public interface IHoldedClient
{
    /// <summary>
    /// False when no <c>HOLDED_API_KEY_V2</c> is configured (PR previews, local dev). Every call
    /// would 401 — a permanent error — so callers skip the work instead of writing off their
    /// queues, and can tell "not configured" apart from "queued" when reporting state.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>Creates a purchase document and returns the new doc id.</summary>
    Task<string> CreatePurchaseDocumentAsync(
        HoldedPurchaseDocumentInput input,
        CancellationToken ct = default);

    /// <summary>Uploads a single attachment to a purchase document.</summary>
    Task UploadAttachmentAsync(
        string documentId,
        HoldedAttachmentInput attachment,
        CancellationToken ct = default);

    /// <summary>Reads a purchase document by id.</summary>
    Task<HoldedPurchaseDocumentDto> GetPurchaseDocumentAsync(
        string documentId,
        CancellationToken ct = default);

    /// <summary>Lists all P&L expense accounts (id + number + name).</summary>
    Task<IReadOnlyList<HoldedExpenseAccountDto>> ListExpenseAccountsAsync(
        CancellationToken ct = default);

    /// <summary>Creates a P&L expense account; returns the new account id.</summary>
    Task<string> CreateExpenseAccountAsync(
        int accountNum, string name, CancellationToken ct = default);

    /// <summary>Lists all purchase documents (cursor-paginated internally).</summary>
    Task<IReadOnlyList<HoldedPurchaseDocListItemDto>> ListPurchaseDocumentsAsync(
        CancellationToken ct = default);

    /// <summary>Ids of purchases still in draft — GET /purchases?approval_status=draft.</summary>
    Task<IReadOnlySet<string>> ListDraftPurchaseIdsAsync(CancellationToken ct = default);

    /// <summary>Creates or updates a contact; returns the contact id.</summary>
    Task<string> UpsertContactAsync(HoldedContactInput input, CancellationToken ct = default);

    /// <summary>Reads one contact; exposes supplierRecord.num (the 400000xx account).</summary>
    Task<HoldedContactDto> GetContactAsync(string contactId, CancellationToken ct = default);

    /// <summary>Lists journal lines from GET v2/ledger-entries across a date window. Paginates internally
    /// via cursor; optionally scoped to one account number.</summary>
    Task<IReadOnlyList<HoldedLedgerLineDto>> ListLedgerEntriesAsync(
        LocalDate from, LocalDate to, int? accountNum = null, CancellationToken ct = default);

    /// <summary>Lists the full chart of accounting accounts with their current totals.</summary>
    Task<IReadOnlyList<HoldedAccountDto>> ListAccountingAccountsAsync(CancellationToken ct = default);

    /// <summary>Reads the current API usage/quota counters.</summary>
    Task<HoldedUsageDto> GetUsageAsync(CancellationToken ct = default);

    /// <summary>Lists all contacts (id + name + supplierRecord.num) for account-number → contact resolution.
    /// Paginates internally by walking `page` until an empty page returns.</summary>
    Task<IReadOnlyList<HoldedContactDto>> ListContactsAsync(CancellationToken ct = default);
}
