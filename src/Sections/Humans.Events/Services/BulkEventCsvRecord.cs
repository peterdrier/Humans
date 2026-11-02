using CsvHelper;

namespace Humans.Events.Services;

/// <summary>
/// Shared by the template download and the parser, so the column set cannot drift. All strings: parsing and
/// validation stay in the parser and validator so per-row errors can be collected.
/// </summary>
/// <remarks>
/// Matched by header name, so uploaders may reorder columns, keep extra ones, or omit optional ones. <c>Barrio</c> and
/// <c>Status</c> are ignored on upload; <c>Id</c> is blank for new events.
/// </remarks>
[CsvRecord]
internal sealed partial class BulkEventCsvRecord
{
    [CsvColumn(Optional = true)] public string Id { get; set; } = string.Empty;
    [CsvColumn(Optional = true)] public string Barrio { get; set; } = string.Empty;
    [CsvColumn(Optional = true)] public string Status { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public string StartTime { get; set; } = string.Empty;
    public string DurationMinutes { get; set; } = string.Empty;
    [CsvColumn(Optional = true)] public string LocationNote { get; set; } = string.Empty;
    [CsvColumn(Optional = true)] public string Host { get; set; } = string.Empty;
    public string IsRecurring { get; set; } = string.Empty;
    [CsvColumn(Optional = true)] public string RecurrenceDays { get; set; } = string.Empty;
    public string PriorityRank { get; set; } = string.Empty;
}
