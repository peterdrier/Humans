using Humans.Base.Interfaces.Repositories;
using Humans.Finance.Contracts;
using Humans.Finance.Domain;
using Humans.Finance.Models;
using NodaTime;

namespace Humans.Finance.Data;

internal interface IHoldedRepository : IRepository
{
    Task<IReadOnlyList<HoldedCategoryMap>> GetCategoryMapAsync(CancellationToken ct = default);
    Task AddCategoryMapAsync(HoldedCategoryMap row, CancellationToken ct = default);

    Task<IReadOnlyList<HoldedManagedAccount>> GetManagedAccountsAsync(CancellationToken ct = default);

    /// <summary>Keyed on the account number, which is how Holded and every caller identify it.</summary>
    Task UpsertManagedAccountAsync(HoldedManagedAccount row, CancellationToken ct = default);

    /// <summary>Removes missing rows and keeps the identity and creation time of retained ones.</summary>
    Task ReplaceDocsAsync(IReadOnlyList<HoldedExpenseDoc> docs, Instant now, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedExpenseDoc>> GetUnmatchedAsync(CancellationToken ct = default);
    Task<IReadOnlyList<HoldedExpenseDoc>> GetMatchedForYearAsync(int calendarYear, CancellationToken ct = default);

    /// <summary>Matched or not; the two reads above are filtered slices. Unordered.</summary>
    Task<IReadOnlyList<HoldedExpenseDoc>> GetAllDocsAsync(CancellationToken ct = default);

    Task<HoldedCreditorContact?> GetCreditorContactByUserAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedCreditorContact>> GetCreditorContactsAsync(CancellationToken ct = default);
    /// <summary>Inserts or updates in place. False only when a concurrent insert won the member's unique slot; nothing
    /// was written and the caller re-reads to adopt the winner.</summary>
    Task<bool> UpsertCreditorContactAsync(HoldedCreditorContact row, Instant now, CancellationToken ct = default);

    /// <summary>Never touches an existing binding. False when the member already has one; the caller re-reads to adopt
    /// it.</summary>
    Task<bool> TryInsertCreditorContactAsync(HoldedCreditorContact row, CancellationToken ct = default);

    /// <summary>Null when there was none.</summary>
    Task<HoldedCreditorContact?> DeleteCreditorContactAsync(Guid userId, CancellationToken ct = default);

    /// <summary>One save: a file without its rows, or the reverse, is never a valid state.</summary>
    Task AddSepaPayoutAsync(
        SepaPayoutFile file, IReadOnlyList<SepaPayoutTransfer> transfers, CancellationToken ct = default);

    /// <summary>Oldest first.</summary>
    Task<IReadOnlyList<SepaPayoutExportRow>> GetSepaPayoutsForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>Unordered, without the file's XML.</summary>
    Task<IReadOnlyList<SepaPayoutTransferRow>> GetSepaPayoutTransferRowsAsync(
        CancellationToken ct = default);

    Task<SepaPayoutTransfer?> GetSepaTransferAsync(Guid transferId, CancellationToken ct = default);

    /// <summary>A null actor is the sweep. The payment ids are comma-joined; null when it posted none.</summary>
    Task SaveSepaTransferBookingAsync(
        Guid transferId, Instant bookedAt, Guid? bookedByUserId,
        string bankMovementId, Instant? reconciledAt, string? holdedPaymentRefs,
        CancellationToken ct = default);

    /// <summary>For a run that posted payments and stopped short of booking, so a retry refuses. Touches nothing
    /// else.</summary>
    Task SaveSepaTransferPaymentRefsAsync(
        Guid transferId, string holdedPaymentRefs, CancellationToken ct = default);

    /// <summary>The service has already checked the row is unbooked.</summary>
    Task SaveSepaTransferWithdrawnAsync(
        Guid transferId, Instant withdrawnAt, Guid withdrawnByUserId, CancellationToken ct = default);

    /// <summary>A reconcile that succeeded on a later pass; the booking is untouched.</summary>
    Task MarkSepaTransferReconciledAsync(
        Guid transferId, Instant reconciledAt, CancellationToken ct = default);

    /// <summary>The persisted singleton, or an unsaved Idle default. Never writes.</summary>
    Task<HoldedDocSyncState> GetOrCreateDocSyncStateAsync(CancellationToken ct = default);
    Task SaveDocSyncStateAsync(HoldedDocSyncState state, CancellationToken ct = default);
}
