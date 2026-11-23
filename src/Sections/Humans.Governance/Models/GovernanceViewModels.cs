using Humans.Google.Contracts;
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

    /// <summary>Volunteer when the viewer holds no tier.</summary>
    public MembershipTier CurrentTier { get; set; }
    public bool IsBoardMember { get; set; }

    /// <summary>The earliest approved application or Board seat; null for non-members.</summary>
    public DateTime? MemberSince { get; set; }

    public int ColaboradorCount { get; set; }
    public int AsociadoCount { get; set; }

    /// <summary>Office holders first, in office order, then by burner name.</summary>
    public IReadOnlyList<Guid> BoardMemberIds { get; set; } = [];
    public IReadOnlyDictionary<Guid, BoardPosition> BoardPositions { get; set; } = new Dictionary<Guid, BoardPosition>();
    public IReadOnlyList<AssemblyVoteListItem> OpenVotes { get; set; } = [];
    public int UnvotedApplicationCount { get; set; }

    /// <summary>The group and files of each body the viewer is in.</summary>
    public IReadOnlyList<GovernanceBodyAccess> YourAccess { get; set; } = [];
}

internal sealed class GovernanceBodyViewModel
{
    public required GovernanceBody Body { get; init; }

    /// <summary>Ordered by burner name; on the Board page, office holders come first in office order.</summary>
    public required IReadOnlyList<Guid> MemberIds { get; init; }

    /// <summary>Board page only: each member's office.</summary>
    public IReadOnlyDictionary<Guid, BoardPosition> Positions { get; init; } = new Dictionary<Guid, BoardPosition>();

    /// <summary>Null when the viewer may not see them, or the body has none.</summary>
    public GovernanceBodyAccess? Links { get; init; }

    /// <summary>Board page only: the viewer may add and end Board seats.</summary>
    public bool CanManageBoard { get; init; }

    /// <summary>The body's group and files for editing; null unless the viewer is Board or Admin.</summary>
    public GovernanceBodyManageModel? Manage { get; init; }
}

internal sealed record GovernanceBodyManageModel(string? GroupPrefix, IReadOnlyList<GovernanceBodyManageFile> Files);

internal sealed record GovernanceBodyManageFile(Guid Id, string Name, string? Url, DrivePermissionLevel Level);
