using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Humans.Calendar.Services.Dtos;
using Humans.Calendar.Contracts;
using NodaTime;

namespace Humans.Calendar.Models;

internal sealed class CalendarEventFormViewModel : ICalendarEventForm
{
    public Guid? Id { get; set; }

    [Required(ErrorMessage = "Validation_Required"), StringLength(200, ErrorMessage = "Validation_MaxLength")]
    public string Title { get; set; } = string.Empty;

    [StringLength(4000, ErrorMessage = "Validation_MaxLength")]
    public string? Description { get; set; }

    [StringLength(500, ErrorMessage = "Validation_MaxLength")]
    public string? Location { get; set; }

    [StringLength(2000, ErrorMessage = "Validation_MaxLength"), Url(ErrorMessage = "Validation_InvalidValue")]
    public string? LocationUrl { get; set; }

    [Required(ErrorMessage = "Validation_Required")]
    public Guid OwningTeamId { get; set; }

    [BindRequired]
    public bool IsAllDay { get; set; }

    public LocalDate? StartDate { get; set; }

    public LocalTime? StartTime { get; set; }

    public LocalDate? EndDate { get; set; }

    public LocalTime? EndTime { get; set; }

    [BindRequired]
    public bool IsRecurring { get; set; }

    [StringLength(500, ErrorMessage = "Validation_MaxLength")]
    public string? RecurrenceRule { get; set; }

    [BindRequired]
    public string TimeZoneId { get; set; } = string.Empty;

    public bool SupportsAllDay => true;

    public bool SupportsRecurrence => true;

    public IReadOnlyList<TeamOption> TeamOptions { get; set; } = [];

    /// <summary>
    /// Empty means form level: the service names a member only for the recurrence pair it validates by hand.
    /// </summary>
    public static string ErrorFieldFor(string? serviceMemberName) => serviceMemberName switch
    {
        nameof(CreateCalendarEventDto.RecurrenceTimezone) => nameof(TimeZoneId),
        nameof(CreateCalendarEventDto.RecurrenceRule) => nameof(RecurrenceRule),
        nameof(CreateCalendarEventDto.OwningTeamId) => nameof(OwningTeamId),
        _ => string.Empty,
    };
}
