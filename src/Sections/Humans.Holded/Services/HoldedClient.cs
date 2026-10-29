using Humans.Holded.Contracts;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Holded.Services;

internal sealed class HoldedClient : IHoldedClient
{
    private readonly HoldedTransport _transport;
    private readonly HoldedPurchasesClient _purchases;
    private readonly HoldedSalesClient _sales;
    private readonly HoldedContactsClient _contacts;
    private readonly HoldedTreasuryClient _treasury;
    private readonly HoldedAccountingClient _accounting;

    public HoldedClient(
        HttpClient http,
        IOptions<HoldedClientOptions> options,
        ILogger<HoldedClient> logger,
        IHoldedCallLog callLog,
        IClock clock)
    {
        _transport = new HoldedTransport(http, options, logger, callLog, clock);
        _purchases = new HoldedPurchasesClient(_transport, logger);
        _sales = new HoldedSalesClient(_transport);
        _contacts = new HoldedContactsClient(_transport, logger);
        _treasury = new HoldedTreasuryClient(_transport, logger);
        _accounting = new HoldedAccountingClient(_transport, logger);
    }

    public bool IsConfigured => _transport.IsConfigured;

    public Task<string> CreatePurchaseDocumentAsync(
        HoldedPurchaseDocumentInput input, CancellationToken ct = default) =>
        _purchases.CreatePurchaseDocumentAsync(input, ct);

    public Task UploadAttachmentAsync(
        string documentId, HoldedAttachmentInput attachment, CancellationToken ct = default) =>
        _purchases.UploadAttachmentAsync(documentId, attachment, ct);

    public Task<HoldedPurchaseDocumentDto> GetPurchaseDocumentAsync(
        string documentId, CancellationToken ct = default) =>
        _purchases.GetPurchaseDocumentAsync(documentId, ct);

    public Task ApprovePurchaseDocumentAsync(string documentId, CancellationToken ct = default) =>
        _purchases.ApprovePurchaseDocumentAsync(documentId, ct);

    public Task DeletePurchaseDocumentAsync(string documentId, CancellationToken ct = default) =>
        _purchases.DeletePurchaseDocumentAsync(documentId, ct);

    public Task<string> PayPurchaseDocumentAsync(
        string documentId, decimal amount, string? treasuryId, LocalDate date, string? description,
        CancellationToken ct = default) =>
        _purchases.PayPurchaseDocumentAsync(documentId, amount, treasuryId, date, description, ct);

    public Task<string> PostLedgerEntryAsync(
        LocalDate date, int debitAccount, int creditAccount, decimal amount, string description,
        CancellationToken ct = default) =>
        _accounting.PostLedgerEntryAsync(date, debitAccount, creditAccount, amount, description, ct);

    public Task<IReadOnlyList<HoldedExpenseAccountDto>> ListExpenseAccountsAsync(
        CancellationToken ct = default) =>
        _accounting.ListExpenseAccountsAsync(ct);

    public Task<string> CreateExpenseAccountAsync(
        int accountNum, string name, CancellationToken ct = default) =>
        _accounting.CreateExpenseAccountAsync(accountNum, name, ct);

    public Task<IReadOnlyList<HoldedPurchaseDocListItemDto>> ListPurchaseDocumentsAsync(
        CancellationToken ct = default) =>
        _purchases.ListPurchaseDocumentsAsync(ct);

    public Task<string> CreateContactAsync(HoldedContactInput input, CancellationToken ct = default) =>
        _contacts.CreateContactAsync(input, ct);

    public Task<string> CreateSalesDocumentAsync(
        HoldedSalesDocumentKind kind, HoldedSalesDocumentInput input, CancellationToken ct = default) =>
        _sales.CreateSalesDocumentAsync(kind, input, ct);

    public Task ApproveSalesDocumentAsync(
        HoldedSalesDocumentKind kind, string documentId, CancellationToken ct = default) =>
        _sales.ApproveSalesDocumentAsync(kind, documentId, ct);

    public Task<IReadOnlyList<string>> FindSalesDocumentIdsByTagAsync(
        HoldedSalesDocumentKind kind, string tag, CancellationToken ct = default) =>
        _sales.FindSalesDocumentIdsByTagAsync(kind, tag, ct);

    public Task<HoldedSalesDocumentDto> GetSalesDocumentAsync(
        HoldedSalesDocumentKind kind, string documentId, CancellationToken ct = default) =>
        _sales.GetSalesDocumentAsync(kind, documentId, ct);

    public Task<HoldedContactDto> GetContactAsync(string contactId, CancellationToken ct = default) =>
        _contacts.GetContactAsync(contactId, ct);

    public Task DeleteContactAsync(string contactId, CancellationToken ct = default) =>
        _contacts.DeleteContactAsync(contactId, ct);

    public Task<IReadOnlyList<HoldedContactDto>> ListContactsAsync(CancellationToken ct = default) =>
        _contacts.ListContactsAsync(ct);

    public Task<IReadOnlyList<HoldedBankMovementDto>> ListBankMovementsAsync(
        string treasuryAccountId, LocalDate from, LocalDate to, CancellationToken ct = default,
        IReadOnlySet<string>? skipUnreadableDateIds = null) =>
        _treasury.ListBankMovementsAsync(treasuryAccountId, from, to, ct, skipUnreadableDateIds);

    public Task ReconcileBankMovementAsync(
        string treasuryAccountId, string movementId,
        IReadOnlyList<HoldedReconcileDocumentRef> documents, CancellationToken ct = default) =>
        _treasury.ReconcileBankMovementAsync(treasuryAccountId, movementId, documents, ct);

    public Task<IReadOnlyList<HoldedLedgerLineDto>> ListLedgerEntriesAsync(
        LocalDate from, LocalDate to, int? accountNum = null, CancellationToken ct = default) =>
        _accounting.ListLedgerEntriesAsync(from, to, accountNum, ct);

    public Task<IReadOnlyList<HoldedAccountDto>> ListAccountingAccountsAsync(
        CancellationToken ct = default) =>
        _accounting.ListAccountingAccountsAsync(ct);

    public Task<HoldedUsageDto> GetUsageAsync(CancellationToken ct = default) =>
        _accounting.GetUsageAsync(ct);
}
