namespace Humans.Teams.Contracts;

public record SyncAffectedUser(Guid UserId, string DisplayName);

public record SyncChange(string Action, SyncAffectedUser User, string Detail);

/// <summary>One step, e.g. "Volunteers" or "Coordinators".</summary>
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

public class SyncReport
{
    public List<SyncStepResult> Steps { get; init; } = [];

    public int TotalChanges => Steps.Sum(s => s.TotalChanges);
    public bool HasChanges => Steps.Any(s => s.HasChanges);
}
