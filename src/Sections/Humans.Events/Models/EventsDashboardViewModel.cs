using NodaTime;

namespace Humans.Events.Models;

internal sealed class EventsDashboardViewModel
{
    public Guid? EventSettingsId { get; set; }
    public int TotalCount { get; set; }
    public int PendingCount { get; set; }
    public int ApprovedCount { get; set; }
    public int RejectedCount { get; set; }
    public int ResubmitRequestedCount { get; set; }
    public int WithdrawnCount { get; set; }

    public List<DayCoverageRow> CoverageByDay { get; set; } = [];
    public List<CategoryCoverageRow> CoverageByCategory { get; set; } = [];
    public List<CampSubmissionRow> TopCamps { get; set; } = [];
}

internal sealed class DayCoverageRow
{
    public LocalDate Date { get; set; }
    public string DayLabel { get; set; } = string.Empty;
    public int ApprovedCount { get; set; }
}

internal sealed class CategoryCoverageRow
{
    public int DisplayOrder { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public int SubmittedCount { get; set; }
    public int ApprovedCount { get; set; }
    public int PendingCount { get; set; }
    public int RejectedCount { get; set; }
}

internal sealed class CampSubmissionRow
{
    public Guid CampId { get; set; }
    public string CampName { get; set; } = string.Empty;
    public int SubmittedCount { get; set; }
    public int ApprovedCount { get; set; }
    public int PendingCount { get; set; }
}
