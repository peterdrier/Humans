using Humans.Base.Constants;
using NodaTime;

namespace Humans.Governance.Domain;

/// <summary>One row per body: its Google group and the Google files its members get.</summary>
internal sealed class GovernanceBodyLinks
{
    public GovernanceBody Body { get; init; }

    /// <summary>The group address before the @; null when the body has no group.</summary>
    public string? GoogleGroupPrefix { get; set; }

    public string? GoogleGroupEmail =>
        GoogleGroupPrefix is { } prefix ? $"{prefix}@{DomainConstants.GoogleGroupDomain}" : null;

    public Instant UpdatedAt { get; set; }

    public List<GovernanceBodyFile> Files { get; init; } = [];
}
