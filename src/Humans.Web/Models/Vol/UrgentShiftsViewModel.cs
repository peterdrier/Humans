using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Web.Models.Vol;

public class UrgentShiftsViewModel
{
    public List<UrgentShiftRow> Shifts { get; set; } = [];
    public EventSettings EventSettings { get; set; } = null!;

    public class UrgentShiftRow
    {
        public Guid ShiftId { get; set; }
        public string DutyTitle { get; set; } = string.Empty;
        public string TeamName { get; set; } = string.Empty;
        public string TeamSlug { get; set; } = string.Empty;
        public int DayOffset { get; set; }
        public LocalTime StartTime { get; set; }
        public Duration Duration { get; set; }
        public int Confirmed { get; set; }
        public int MaxVolunteers { get; set; }
        public ShiftPriority Priority { get; set; }
        public double UrgencyScore { get; set; }
    }
}
