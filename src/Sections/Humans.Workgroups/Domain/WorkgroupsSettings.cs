namespace Humans.Workgroups.Domain;

internal sealed class WorkgroupsSettings
{
    public const int SingletonId = 1;
    public int Id { get; init; } = SingletonId;
    public required string RootDriveFolderId { get; set; }
}
