using AwesomeAssertions;
using Humans.Calendar.Contracts;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Localization;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Calendar.Tests.Contracts;

/// <summary>The shared form's one conversion between posted local fields and stored times.</summary>
public sealed class CalendarEventFormTests
{
    private static readonly IStringLocalizer<CalendarResource> Localizer = CreateLocalizer();

    [HumansFact]
    public void Timed_fields_are_read_in_the_chosen_zone()
    {
        var form = Timed("Europe/Madrid", new TimeOnly(19, 0), new TimeOnly(20, 0));

        var times = CalendarEventForm.Resolve(form, new ModelStateDictionary(), Localizer);

        // June in Madrid is UTC+2.
        times!.Start.Should().Be(Instant.FromUtc(2026, 6, 2, 17, 0));
        times.End.Should().Be(Instant.FromUtc(2026, 6, 2, 18, 0));
    }

    [HumansFact]
    public void SetTimed_round_trips_through_Resolve()
    {
        var zone = DateTimeZoneProviders.Tzdb["America/New_York"];
        var start = Instant.FromUtc(2026, 11, 3, 23, 30);
        var form = new Form();

        CalendarEventForm.SetTimed(form, start, start + Duration.FromHours(1), zone);
        var times = CalendarEventForm.Resolve(form, new ModelStateDictionary(), Localizer);

        times!.Start.Should().Be(start);
        times.End.Should().Be(start + Duration.FromHours(1));
    }

    [HumansFact]
    public void An_end_before_the_start_is_a_field_error()
    {
        var form = Timed("UTC", new TimeOnly(11, 0), new TimeOnly(10, 0));
        var modelState = new ModelStateDictionary();

        CalendarEventForm.Resolve(form, modelState, Localizer).Should().BeNull();

        modelState[nameof(ICalendarEventForm.EndTime)]!.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be("Calendar_EndBeforeStart");
    }

    [HumansFact]
    public void All_day_is_ignored_when_the_host_does_not_support_it()
    {
        var form = Timed("UTC", new TimeOnly(10, 0), new TimeOnly(11, 0));
        form.IsAllDay = true;
        form.AllDay = false;

        var times = CalendarEventForm.Resolve(form, new ModelStateDictionary(), Localizer);

        times!.Start.Should().Be(Instant.FromUtc(2026, 6, 2, 10, 0));
        times.StartDate.Should().BeNull();
    }

    [HumansTheory]
    [InlineData(true, "FREQ=WEEKLY", "UTC")]
    [InlineData(false, null, null)]
    public void Recurrence_resolves_only_when_the_host_supports_it(bool supported, string? rule, string? zone)
    {
        var form = Timed("UTC", new TimeOnly(10, 0), new TimeOnly(11, 0));
        form.Recurrence = supported;
        form.IsRecurring = true;
        form.RecurrenceRule = "FREQ=WEEKLY";

        var times = CalendarEventForm.Resolve(form, new ModelStateDictionary(), Localizer);

        times!.RecurrenceRule.Should().Be(rule);
        times.RecurrenceTimezone.Should().Be(zone);
    }

    private static Form Timed(string zone, TimeOnly start, TimeOnly end) => new()
    {
        StartDate = new DateOnly(2026, 6, 2),
        StartTime = start,
        EndDate = new DateOnly(2026, 6, 2),
        EndTime = end,
        TimeZoneId = zone,
    };

    private static IStringLocalizer<CalendarResource> CreateLocalizer()
    {
        var localizer = Substitute.For<IStringLocalizer<CalendarResource>>();
        localizer[Arg.Any<string>()].Returns(c => new LocalizedString((string)c[0], (string)c[0]));
        return localizer;
    }

    private sealed class Form : ICalendarEventForm
    {
        public string Title { get; set; } = "Sync";
        public string? Description { get; set; }
        public string? Location { get; set; }
        public string? LocationUrl { get; set; }
        public bool IsAllDay { get; set; }
        public DateOnly? StartDate { get; set; }
        public TimeOnly? StartTime { get; set; }
        public DateOnly? EndDate { get; set; }
        public TimeOnly? EndTime { get; set; }
        public string TimeZoneId { get; set; } = "UTC";
        public bool IsRecurring { get; set; }
        public string? RecurrenceRule { get; set; }
        public bool AllDay { get; set; } = true;
        public bool Recurrence { get; set; } = true;
        public bool SupportsAllDay => AllDay;
        public bool SupportsRecurrence => Recurrence;
    }
}
