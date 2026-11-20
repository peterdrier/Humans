using Humans.Holded.Contracts;
using Humans.Holded.Data;
using NodaTime;
using static Humans.Holded.Services.HoldedJson;

namespace Humans.Holded.Services;

/// <summary>The cross-section write face: id-keyed document/contact writes and chart-number resolution.</summary>
internal sealed class HoldedWrite(
    IHoldedClient client,
    IHoldedMirrorRepository repo,
    IHoldedService holded,
    ILogger<HoldedWrite> logger) : IHoldedWrite
{
    public async Task<HoldedExpenseAccountOutcome> EnsureExpenseAccountAsync(
        int number, string name, CancellationToken ct = default)
    {
        if ((await repo.GetAccountsAsync(ct)).FirstOrDefault(a => a.Number == number) is { } mirrored)
            return new HoldedExpenseAccountOutcome(mirrored.HoldedId, Created: false);

        // The mirror is as old as the last sync; an account made in Holded's UI since then must not get a twin.
        var live = (await client.ListExpenseAccountsAsync(ct)).FirstOrDefault(a => a.AccountNum == number);
        var id = live?.Id ?? await client.CreateExpenseAccountAsync(number, name, ct);
        await holded.SyncChartAsync(ct);
        return new HoldedExpenseAccountOutcome(id, Created: live is null);
    }

    public async Task<string> CreateContactAsync(HoldedContactInput input, CancellationToken ct = default)
    {
        var id = await client.CreateContactAsync(input, ct);
        await RefreshContactsAsync(ct);
        return id;
    }

    public async Task DeleteContactAsync(string contactId, CancellationToken ct = default)
    {
        await client.DeleteContactAsync(contactId, ct);
        await RefreshContactsAsync(ct);
    }

    public Task<string> CreatePurchaseDocumentAsync(
        HoldedPurchaseDocumentInput input, CancellationToken ct = default) =>
        client.CreatePurchaseDocumentAsync(input, ct);

    public Task UploadPurchaseAttachmentAsync(
        string documentId, HoldedAttachmentInput attachment, CancellationToken ct = default) =>
        client.UploadAttachmentAsync(documentId, attachment, ct);

    public async Task EnsurePurchaseDocumentApprovedAsync(string documentId, CancellationToken ct = default)
    {
        if ((await client.GetPurchaseDocumentAsync(documentId, ct)).ApprovedAt is null)
            await client.ApprovePurchaseDocumentAsync(documentId, ct);
    }

    public Task DeletePurchaseDocumentByIdAsync(string documentId, CancellationToken ct = default) =>
        client.DeletePurchaseDocumentAsync(documentId, ct);

    public async Task<IReadOnlyList<(HoldedSalesDocumentKind Kind, HoldedSalesDocumentDto Document)>>
        FindSalesDocumentsByTagAsync(string tag, CancellationToken ct = default)
    {
        var found = new List<(HoldedSalesDocumentKind, HoldedSalesDocumentDto)>();
        foreach (var kind in Enum.GetValues<HoldedSalesDocumentKind>())
            foreach (var id in await client.FindSalesDocumentIdsByTagAsync(kind, tag, ct))
                found.Add((kind, await client.GetSalesDocumentAsync(kind, id, ct)));
        return found;
    }

    public async Task<HoldedSalesDocumentDto> IssueSalesDocumentAsync(
        HoldedSalesDocumentKind kind, HoldedSalesDocumentInput input, CancellationToken ct = default)
    {
        if (input.Lines.Any(l => l.AccountNum is not null))
        {
            var idByNum = (await repo.GetAccountsAsync(ct)).ToDictionary(a => a.Number, a => a.HoldedId);
            input = input with
            {
                Lines = input.Lines.Select(l => l.AccountNum is not { } num ? l : l with
                {
                    AccountId = idByNum.GetValueOrDefault(num) ?? throw new HoldedPermanentException(
                        $"Account {num} is not in the chart mirror; sync the chart first."),
                }).ToList(),
            };
        }

        var id = await client.CreateSalesDocumentAsync(kind, input, ct);
        await client.ApproveSalesDocumentAsync(kind, id, ct);
        return await client.GetSalesDocumentAsync(kind, id, ct);
    }

    public async Task<HoldedSalesDocumentDto> ApproveSalesDocumentAsync(
        HoldedSalesDocumentKind kind, string documentId, CancellationToken ct = default)
    {
        await client.ApproveSalesDocumentAsync(kind, documentId, ct);
        return await client.GetSalesDocumentAsync(kind, documentId, ct);
    }

    /// <summary>The write stands either way; a mirror left behind catches up on the next sync.</summary>
    private async Task RefreshContactsAsync(CancellationToken ct)
    {
        try
        {
            if (!await holded.SyncContactsAsync(ct))
                logger.LogInformation("A contact sync was already running; the mirror catches up on the next one.");
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogWarning(ex, "The contact mirror could not be refreshed after a contact write.");
        }
    }

    public Task<string> PayPurchaseDocumentAsync(
        string documentId, decimal amount, string? treasuryId, LocalDate date, string? description,
        CancellationToken ct = default) =>
        client.PayPurchaseDocumentAsync(documentId, amount, treasuryId, date, description, ct);

    public Task ReconcileBankMovementAsync(
        string treasuryAccountId, string movementId, IReadOnlyList<HoldedReconcileDocumentRef> documents,
        CancellationToken ct = default) =>
        client.ReconcileBankMovementAsync(treasuryAccountId, movementId, documents, ct);

    public async Task<HoldedJournalEntryOutcome> PostJournalEntryAsync(
        LocalDate date, int debitAccount, int creditAccount, decimal amount, string description,
        CancellationToken ct = default)
    {
        var id = await client.PostLedgerEntryAsync(date, debitAccount, creditAccount, amount, description, ct);
        return new HoldedJournalEntryOutcome(!id.StartsWith(UnconfirmedPaymentRefPrefix, StringComparison.Ordinal));
    }

}
