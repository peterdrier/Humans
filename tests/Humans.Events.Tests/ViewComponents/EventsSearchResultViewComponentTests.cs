using AwesomeAssertions;
using Humans.Events.Contracts;
using Humans.Events.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using NodaTime;
using NSubstitute;

namespace Humans.Events.Tests.ViewComponents;

/// <summary>
/// Covers <see cref="EventsSearchResultViewComponent"/>: the global-search row for an event.
/// Callers pass an event id and nothing else, so the
/// cache-served by-id fetch and the empty-content fallback are the whole behaviour.
/// </summary>
public class EventsSearchResultViewComponentTests
{
    private readonly IEventService _events = Substitute.For<IEventService>();

    [HumansFact]
    public async Task Invoke_ApprovedId_RendersEvent()
    {
        var wanted = Approved("Sunrise Yoga");
        _events.GetApprovedEventByIdAsync(wanted.Id, Arg.Any<CancellationToken>()).Returns(wanted);

        var result = await CreateComponent().InvokeAsync(wanted.Id);

        var model = result.Should().BeOfType<ViewViewComponentResult>()
            .Subject.ViewData!.Model.Should().BeOfType<EventsSearchResultViewModel>().Subject;
        model.Title.Should().Be("Sunrise Yoga");
        model.CategoryName.Should().Be("Music");
    }

    [HumansFact]
    public async Task Invoke_IdNotApproved_RendersNothing()
    {
        // Unapproved, deleted, or feature-gated away — all reach here as a cache miss.
        _events.GetApprovedEventByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((ApprovedEventView?)null);

        var result = await CreateComponent().InvokeAsync(Guid.NewGuid());

        result.Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
    }

    private EventsSearchResultViewComponent CreateComponent() => new(_events)
    {
        ViewComponentContext = new ViewComponentContext
        {
            ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() },
        },
    };

    private static ApprovedEventView Approved(string title) => new(
        Id: Guid.NewGuid(), CampId: Guid.NewGuid(), GuideSharedVenueId: null, SubmitterUserId: Guid.NewGuid(),
        CategoryId: Guid.NewGuid(), CategorySlug: "music", CategoryName: "Music", CategoryIsSensitive: false,
        VenueName: null, Title: title, Description: string.Empty, LocationNote: null, Host: null,
        StartAt: Instant.FromUtc(2026, 8, 1, 10, 0), DurationMinutes: 60, IsRecurring: false, RecurrenceDays: null,
        PriorityRank: 0);
}
