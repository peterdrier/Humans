using CsvHelper.Configuration;

namespace Humans.Events.Services;

/// <summary>
/// Shared by the template download and the parser, so the column set cannot drift. All strings: parsing and
/// validation stay in the parser and validator so per-row errors can be collected.
/// </summary>
internal sealed class BulkEventCsvRecord
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
/// Matched by header name, so uploaders may reorder columns, keep extra ones, or omit optional ones. <c>Barrio</c> and
/// <c>Status</c> are ignored on upload; <c>Id</c> is blank for new events.
/// </summary>
internal sealed class BulkEventCsvRecordMap : ClassMap<BulkEventCsvRecord>
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
