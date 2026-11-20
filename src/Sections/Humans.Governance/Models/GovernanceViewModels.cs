using Humans.Governance.Contracts;
using Humans.Governance.Domain;
using Humans.Governance.Services.Dtos;
using Humans.Governance.Services;
using NodaTime;

namespace Humans.Governance.Models;

internal sealed class GovernanceIndexViewModel
{
    public Guid UserId { get; set; }

    /// <summary>
    /// Statutes content by language code (e.g., "es" → markdown, "en" → markdown).
    /// </summary>
    public Dictionary<string, string> StatutesContent { get; set; } = new(StringComparer.Ordinal);

    public bool HasApplication { get; set; }
    public ApplicationStatus? ApplicationStatus { get; set; }
    public MembershipTier? ApplicationTier { get; set; }
    public DateTime? ApplicationSubmittedAt { get; set; }
    public DateTime? ApplicationResolvedAt { get; set; }
    public LocalDate? ApplicationTermExpiresAt { get; set; }
    public string? ApplicationStatusBadgeClass { get; set; }
    public bool CanApply { get; set; }
    public bool IsApprovedColaborador { get; set; }

    public int ColaboradorCount { get; set; }
    public int AsociadoCount { get; set; }

    public IReadOnlyList<Guid> BoardMemberIds { get; set; } = [];
    public IReadOnlyList<AssemblyVoteListItem> OpenVotes { get; set; } = [];
    public int UnvotedApplicationCount { get; set; }

    /// <summary>The group and files of each body the viewer is in.</summary>
    public IReadOnlyList<GovernanceBodyAccess> YourAccess { get; set; } = [];
}

internal sealed class GovernanceBodyViewModel
{
    public required GovernanceBody Body { get; init; }

    /// <summary>Ordered by burner name.</summary>
    public required IReadOnlyList<Guid> MemberIds { get; init; }

    /// <summary>Null when the viewer may not see them, or the body has none.</summary>
    public GovernanceBodyAccess? Links { get; init; }
}
