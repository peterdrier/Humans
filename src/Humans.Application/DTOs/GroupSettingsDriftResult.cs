namespace Humans.Application.DTOs;

/// <summary>
/// A single setting that differs from the expected value.
/// </summary>
public record GroupSettingDrift(string SettingName, string ExpectedValue, string ActualValue);

/// <summary>
/// Drift report for a single Google Group's settings.
/// </summary>
public class GroupSettingsDriftReport
{
    public string GroupEmail { get; init; } = string.Empty;
    public string GroupName { get; init; } = string.Empty;
    public string? Url { get; init; }
    public Guid ResourceId { get; init; }
    public string? ErrorMessage { get; init; }
    public List<GroupSettingDrift> Drifts { get; init; } = [];

    public bool HasDrift => Drifts.Count > 0;
    public bool HasError => ErrorMessage != null;
    public bool IsOk => !HasDrift && !HasError;
}

/// <summary>
/// Aggregated result of checking group settings across all groups.
/// </summary>
public class GroupSettingsDriftResult
{
    public List<GroupSettingsDriftReport> Reports { get; init; } = [];
    public int TotalGroups => Reports.Count;
    public int OkCount => Reports.Count(r => r.IsOk);
    public int DriftCount => Reports.Count(r => r.HasDrift);
    public int ErrorCount => Reports.Count(r => r.HasError);
    public bool Skipped { get; init; }
    public string? SkipReason { get; init; }
}
