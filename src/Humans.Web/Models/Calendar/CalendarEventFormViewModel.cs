using System.ComponentModel.DataAnnotations;

namespace Humans.Web.Models.Calendar;

public class CalendarEventFormViewModel
{
    public Guid? Id { get; set; }

    [Required, StringLength(200)]
    public string Title { get; set; } = string.Empty;

    [StringLength(4000)]
    public string? Description { get; set; }

    [StringLength(500)]
    public string? Location { get; set; }

    [StringLength(2000), Url]
    public string? LocationUrl { get; set; }

    [Required]
    public Guid OwningTeamId { get; set; }

    public DateTime? StartLocal { get; set; }

    public DateTime? EndLocal { get; set; }

    // Date-only inputs used when IsAllDay is true. EndDateLocal is inclusive (the
    // event covers every day from StartDateLocal through EndDateLocal). The controller
    // normalizes to half-open [Start 00:00, EndDate+1 00:00) when persisting.
    public DateTime? StartDateLocal { get; set; }

    public DateTime? EndDateLocal { get; set; }

    public bool IsAllDay { get; set; }

    public bool IsRecurring { get; set; }

    public string? RecurrenceRule { get; set; }

    [Required]
    public string RecurrenceTimezone { get; set; } = "Europe/Madrid";

    public IReadOnlyList<TeamOption> TeamOptions { get; set; } = Array.Empty<TeamOption>();
}
