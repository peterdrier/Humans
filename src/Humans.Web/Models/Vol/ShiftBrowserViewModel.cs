using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Web.Models.Vol;

public class ShiftBrowserViewModel
{
    public EventSettings EventSettings { get; set; } = null!;
    public List<DepartmentShiftGroup> Departments { get; set; } = [];
    public List<DepartmentOption> AllDepartments { get; set; } = [];
    public Guid? FilterDepartmentId { get; set; }
    public string? FilterFromDate { get; set; }
    public string? FilterToDate { get; set; }
    public string? FilterPeriod { get; set; }
    public bool ShowFullShifts { get; set; }
    public HashSet<Guid> UserSignupShiftIds { get; set; } = [];
    public Dictionary<Guid, SignupStatus> UserSignupStatuses { get; set; } = new();
    public bool ShowSignups { get; set; }
    public bool IsPrivileged { get; set; }
}
