using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Web.Models.Vol;

public class ChildTeamDetailViewModel
{
    public Team ChildTeam { get; set; } = null!;
    public Team Department { get; set; } = null!;
    public List<TeamMember> Members { get; set; } = [];
    public List<RotaShiftGroup> Rotas { get; set; } = [];
    public List<TeamJoinRequest> PendingRequests { get; set; } = [];
    public bool IsCoordinator { get; set; }
    public EventSettings EventSettings { get; set; } = null!;
    public HashSet<Guid> UserSignupShiftIds { get; set; } = [];
    public Dictionary<Guid, SignupStatus> UserSignupStatuses { get; set; } = new();
}
