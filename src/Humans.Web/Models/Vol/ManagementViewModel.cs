using Humans.Domain.Entities;

namespace Humans.Web.Models.Vol;

public class ManagementViewModel
{
    public bool SystemOpen { get; set; }
    public int? VolunteerCap { get; set; }
    public int ConfirmedVolunteerCount { get; set; }
    public EventSettings EventSettings { get; set; } = null!;
}
