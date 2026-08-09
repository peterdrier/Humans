namespace Humans.Events.Services.Dtos;

/// <summary>
/// A single parsed data row from a barrio bulk-upload CSV. <see cref="Id"/> is
/// null for new events and set when editing an existing one.
/// </summary>
internal sealed record BulkCsvRow(
    int RowNumber,
    Guid? Id,
    string Title,
    string Description,
    string Category,
    string Date,
    string StartTime,
    int DurationMinutes,
    string? LocationNote,
    string? Host,
    bool IsRecurring,
    string? RecurrenceDays,
    int PriorityRank);

/// <summary>Per-row validation failure surfaced back to the uploader.</summary>
internal sealed record BulkImportRowError(int RowNumber, string Title, IReadOnlyList<string> Errors);

/// <summary>
/// Outcome of a barrio bulk import. When <see cref="Errors"/> is non-empty the
/// import was rejected wholesale and nothing was written.
/// </summary>
internal sealed record BulkImportResult(
    IReadOnlyList<BulkImportRowError> Errors,
    int CreatedCount,
    int UpdatedCount)
{
    /// <summary>True when validation failed and no events were persisted.</summary>
    public bool HasErrors => Errors.Count > 0;
}
