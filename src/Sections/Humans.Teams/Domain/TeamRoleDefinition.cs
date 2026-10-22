using NodaTime;
using Humans.Base.Attributes;
using Humans.Teams.Contracts;
namespace Humans.Teams.Domain;

internal sealed class TeamRoleDefinition
{
    public Guid Id { get; init; }

    public Guid TeamId { get; init; }

    public Team Team { get; set; } = null!;

    public string Name { get; set; } = string.Empty;

    [MarkdownContent]
    public string? Description { get; set; }

    public int SlotCount { get; set; } = 1;

    /// <summary>Whole hours per year. Informational only: gates nothing.</summary>
    public int? EstimatedHours { get; set; }

    /// <summary>One per slot, by slot index.</summary>
    public List<SlotPriority> Priorities { get; set; } = [];

    public int SortOrder { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }

    /// <summary>Roster page filter.</summary>
    public RolePeriod Period { get; set; } = RolePeriod.YearRound;

    /// <summary>When false, hidden from volunteer-facing views; coordinators and admins still see it.</summary>
    public bool IsPublic { get; set; } = true;

    /// <summary>At most one per team. Assigning a member makes their TeamMemberRole Coordinator.</summary>
    public bool IsManagement { get; set; }

    public ICollection<TeamRoleAssignment> Assignments { get; } = new List<TeamRoleAssignment>();
}
