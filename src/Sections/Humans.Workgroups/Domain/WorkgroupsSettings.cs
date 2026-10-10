namespace Humans.Workgroups.Domain;

/// <summary>The section's single root folder setting, saved by its admin form.</summary>
internal sealed class WorkgroupsSettings
{
    public const int SingletonId = 1;
    public int Id { get; init; } = SingletonId;
    public required string RootDriveFolderId { get; set; }
}
