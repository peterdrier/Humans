using CsvHelper.Configuration;

namespace Humans.Application.Events;

/// <summary>
/// The CSV shape of one barrio bulk-upload row — the single contract shared by
/// the template download (writer) and <see cref="BulkEventCsvParser"/>
/// (reader), so the column set can never drift between the two. All columns
/// are strings: value parsing and validation stay in the parser and
/// <c>EventService.ValidateBulkRows</c> so per-row error messages can be
/// collected instead of failing on the first bad cell.
/// </summary>
public sealed class BulkEventCsvRecord
{
    public string Id { get; set; } = string.Empty;
    public string Barrio { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public string StartTime { get; set; } = string.Empty;
    public string DurationMinutes { get; set; } = string.Empty;
    public string LocationNote { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string IsRecurring { get; set; } = string.Empty;
    public string RecurrenceDays { get; set; } = string.Empty;
    public string PriorityRank { get; set; } = string.Empty;
}

/// <summary>
/// Column map for <see cref="BulkEventCsvRecord"/>. Matching is by header name
/// (case- and whitespace-forgiving via the shared read config), so uploaders
/// may reorder columns, keep extra working columns, or omit the optional ones.
/// <c>Barrio</c> and <c>Status</c> are informational on download and ignored on
/// upload; <c>Id</c> is blank for new events.
/// </summary>
public sealed class BulkEventCsvRecordMap : ClassMap<BulkEventCsvRecord>
{
    public BulkEventCsvRecordMap()
    {
        Map(m => m.Id).Name("Id").Optional();
        Map(m => m.Barrio).Name("Barrio").Optional();
        Map(m => m.Status).Name("Status").Optional();
        Map(m => m.Title).Name("Title");
        Map(m => m.Description).Name("Description");
        Map(m => m.Category).Name("Category");
        Map(m => m.Date).Name("Date");
        Map(m => m.StartTime).Name("StartTime");
        Map(m => m.DurationMinutes).Name("DurationMinutes");
        Map(m => m.LocationNote).Name("LocationNote").Optional();
        Map(m => m.Host).Name("Host").Optional();
        Map(m => m.IsRecurring).Name("IsRecurring");
        Map(m => m.RecurrenceDays).Name("RecurrenceDays").Optional();
        Map(m => m.PriorityRank).Name("PriorityRank");
    }
}
