using Humans.Google.Contracts;

namespace Humans.Governance.Domain;

/// <summary>A Drive folder, shared drive or file the body's members get, at <see cref="PermissionLevel"/>.</summary>
internal sealed class GovernanceBodyFile
{
    public Guid Id { get; init; }

    public GovernanceBody Body { get; init; }

    public string GoogleId { get; init; } = string.Empty;

    public GoogleResourceType ResourceType { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Url { get; init; }

    public DrivePermissionLevel PermissionLevel { get; init; }
}
