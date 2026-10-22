using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Humans.Calendar.Services.Dtos;
using NodaTime;

namespace Humans.Calendar.Models;

internal sealed class CalendarEventFormViewModel
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

    public DateTime? StartLocal { get; set; }

    public DateTime? EndLocal { get; set; }

    // All-day inputs. EndDateLocal is inclusive; the service stores an exclusive end.
    public DateTime? StartDateLocal { get; set; }

    public DateTime? EndDateLocal { get; set; }

    public bool IsAllDay { get; set; }

    [BindRequired]
    public bool IsRecurring { get; set; }

    [StringLength(500, ErrorMessage = "Validation_MaxLength")]
    public string? RecurrenceRule { get; set; }

    [Required(ErrorMessage = "Validation_Required")]
    [BindRequired]
    public string RecurrenceTimezone { get; set; } = "Europe/Madrid";

    public IReadOnlyList<TeamOption> TeamOptions { get; set; } = [];

    /// <summary>
    /// Null when the posted text names no IANA zone, including a cleared box: <c>GetZoneOrNull(null)</c> throws.
    /// </summary>
    public static DateTimeZone? TryResolveZone(string? tz) =>
        string.IsNullOrWhiteSpace(tz) ? null : DateTimeZoneProviders.Tzdb.GetZoneOrNull(tz);

    /// <summary>
    /// Empty means form level: the service names a member only for the recurrence pair it validates by hand.
    /// </summary>
    public static string ErrorFieldFor(string? serviceMemberName) => serviceMemberName switch
    {
        nameof(CreateCalendarEventDto.RecurrenceTimezone) => nameof(RecurrenceTimezone),
        nameof(CreateCalendarEventDto.RecurrenceRule) => nameof(RecurrenceRule),
        nameof(CreateCalendarEventDto.OwningTeamId) => nameof(OwningTeamId),
        _ => string.Empty,
    };
}
