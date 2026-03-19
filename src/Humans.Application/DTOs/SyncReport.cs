namespace Humans.Application.DTOs;

/// <summary>
/// A person affected by a sync step, with enough info to render a link.
/// </summary>
public record SyncAffectedUser(Guid UserId, string DisplayName);

/// <summary>
/// A single change within a sync step.
/// </summary>
public record SyncChange(string Action, SyncAffectedUser User, string Detail);

/// <summary>
/// Result of a single sync step (e.g., "Volunteers", "Coordinators").
/// </summary>
public record SyncStepResult(string StepName)
{
    public List<SyncChange> Changes { get; init; } = [];

    public int TotalChanges => Changes.Count;
    public bool HasChanges => Changes.Count > 0;

    public void Added(Guid userId, string displayName, string? detail = null) =>
        Changes.Add(new SyncChange("Added", new SyncAffectedUser(userId, displayName), detail ?? ""));

    public void Removed(Guid userId, string displayName, string? detail = null) =>
        Changes.Add(new SyncChange("Removed", new SyncAffectedUser(userId, displayName), detail ?? ""));

    public void Fixed(Guid userId, string displayName, string detail) =>
        Changes.Add(new SyncChange("Fixed", new SyncAffectedUser(userId, displayName), detail));
}

/// <summary>
/// Aggregate result of a full system team sync run.
/// </summary>
public class SyncReport
{
    public List<SyncStepResult> Steps { get; init; } = [];

    public int TotalChanges => Steps.Sum(s => s.TotalChanges);
    public bool HasChanges => Steps.Any(s => s.HasChanges);
}
