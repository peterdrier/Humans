using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Web.Models.Vol;

public class MyShiftsViewModel
{
    public List<MyShiftRow> Shifts { get; set; } = [];
    public EventSettings EventSettings { get; set; } = null!;

    public class MyShiftRow
    {
        public Guid SignupId { get; set; }
        public string DutyTitle { get; set; } = string.Empty;
        public string TeamName { get; set; } = string.Empty;
        public Instant AbsoluteStart { get; set; }
        public Instant AbsoluteEnd { get; set; }
        public SignupStatus Status { get; set; }
        public bool CanBail => Status is SignupStatus.Confirmed or SignupStatus.Pending;
    }
}
