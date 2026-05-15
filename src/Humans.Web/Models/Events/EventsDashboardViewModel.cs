namespace Humans.Web.Models.Events;

public class EventsDashboardViewModel
{
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

public class DayCoverageRow
{
    public string DayLabel { get; set; } = string.Empty;
    public int ApprovedCount { get; set; }
}

public class CategoryCoverageRow
{
    public string CategoryName { get; set; } = string.Empty;
    public int SubmittedCount { get; set; }
    public int ApprovedCount { get; set; }
    public int PendingCount { get; set; }
    public int RejectedCount { get; set; }
}

public class CampSubmissionRow
{
    public string CampName { get; set; } = string.Empty;
    public int SubmittedCount { get; set; }
    public int ApprovedCount { get; set; }
    public int PendingCount { get; set; }
}
