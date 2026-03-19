using Humans.Domain.Entities;

namespace Humans.Web.Models.Vol;

public class DepartmentDetailViewModel
{
    public Team Department { get; set; } = null!;
    public List<ChildTeamCard> ChildTeams { get; set; } = [];
    public bool IsCoordinator { get; set; }
    public EventSettings EventSettings { get; set; } = null!;

    public class ChildTeamCard
    {
        public Guid TeamId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public int MemberCount { get; set; }
        public int TotalSlots { get; set; }
        public int FilledSlots { get; set; }
        public int PendingRequestCount { get; set; }
    }
}
