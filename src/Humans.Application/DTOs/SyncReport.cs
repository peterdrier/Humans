namespace Humans.Application.DTOs;

/// <summary>
/// Result of a single sync step (e.g., "Volunteers", "Coordinators").
/// </summary>
public record SyncStepResult(string StepName)
{
    public List<string> Added { get; } = [];
    public List<string> Removed { get; } = [];
    public List<string> Fixed { get; } = [];
    public List<string> Skipped { get; } = [];

    public int TotalChanges => Added.Count + Removed.Count + Fixed.Count;
    public bool HasChanges => TotalChanges > 0;
}

/// <summary>
/// Aggregate result of a full system team sync run.
/// </summary>
public class SyncReport
{
    public List<SyncStepResult> Steps { get; } = [];

    public int TotalAdded => Steps.Sum(s => s.Added.Count);
    public int TotalRemoved => Steps.Sum(s => s.Removed.Count);
    public int TotalFixed => Steps.Sum(s => s.Fixed.Count);
    public bool HasChanges => Steps.Any(s => s.HasChanges);
}
