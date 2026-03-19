namespace Humans.Web.Models.Vol;

public class TeamsOverviewViewModel
{
    public List<DepartmentCard> Departments { get; set; } = [];

    public class DepartmentCard
    {
        public Guid TeamId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public string? Description { get; set; }
        public int ChildTeamCount { get; set; }
        public int TotalSlots { get; set; }
        public int FilledSlots { get; set; }
    }
}
