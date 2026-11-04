using Humans.Base.Interfaces;
using Humans.Pleo.Models;

namespace Humans.Pleo.Services;

internal enum PleoSyncOutcome { NotConfigured, Busy, Synced }

internal interface IPleoAdminService : IApplicationService
{
    /// <summary>The full sweep, shared by the nightly job and Sync now. A failure is recorded on the sync state and
    /// rethrown; a sweep already running makes this one return <see cref="PleoSyncOutcome.Busy"/>.</summary>
    Task<PleoSyncOutcome> SyncAsync(CancellationToken ct = default);

    Task<PleoSummary> GetSummaryAsync(CancellationToken ct = default);
    Task<PleoEntriesPage> GetEntriesPageAsync(PleoEntriesFilter filter, CancellationToken ct = default);

    /// <summary>Lists the receipts live; a failed lookup is reported on the view, not thrown.</summary>
    Task<PleoEntryView?> GetEntryViewAsync(string id, CancellationToken ct = default);

    /// <summary>Null when the entry is not mirrored or the file can't be fetched.</summary>
    Task<PleoReceiptFile?> GetReceiptFileAsync(string entryId, string receiptId, CancellationToken ct = default);

    Task<PleoCategoriesPage> GetCategoriesPageAsync(CancellationToken ct = default);

    /// <summary>Each returns null on success, else why it was refused.</summary>
    Task<string?> LinkCategoryAsync(string pleoCategoryId, Guid categoryId, Guid userId, CancellationToken ct = default);

    Task<string?> UnlinkCategoryAsync(Guid categoryId, Guid userId, CancellationToken ct = default);
    Task<string?> AddCategoryAsync(string name, string holdedCode, Guid userId, CancellationToken ct = default);
}
