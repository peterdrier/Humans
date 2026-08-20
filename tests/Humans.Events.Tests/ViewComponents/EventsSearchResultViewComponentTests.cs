using AwesomeAssertions;
using Humans.Events.Contracts;
using Humans.Events.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NodaTime;
using NSubstitute;

namespace Humans.Events.Tests.ViewComponents;

/// <summary>
/// Covers <see cref="EventsSearchResultViewComponent"/>: the global-search row for an event.
/// Callers pass an event id and nothing else (nobodies-collective/Humans#1062), so picking
/// that event out of the cache-served approved list is the behaviour.
/// </summary>
public class EventsSearchResultViewComponentTests
{
    private readonly IEventServiceRead _events = Substitute.For<IEventServiceRead>();

    [HumansFact]
    public async Task Renders_the_matching_approved_event()
    {
        var wanted = Approved("Sunrise Yoga");
        StubApproved(Approved("Something Else"), wanted);

        var result = await new EventsSearchResultViewComponent(_events).InvokeAsync(wanted.Id);

        var model = result.Should().BeOfType<ViewViewComponentResult>()
            .Subject.ViewData!.Model.Should().BeOfType<EventsSearchResultViewModel>().Subject;
        model.Title.Should().Be("Sunrise Yoga");
        model.CategoryName.Should().Be("Music");
    }

    [HumansFact]
    public async Task Renders_nothing_when_the_id_is_not_approved()
    {
        // Unapproved, deleted, or feature-gated away — all reach here as "not in the list".
        StubApproved(Approved("Something Else"));

        var result = await new EventsSearchResultViewComponent(_events).InvokeAsync(Guid.NewGuid());

        result.Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
    }

    private void StubApproved(params ApprovedEventView[] events) =>
        _events.GetApprovedEventsAsync(null, null, null, null,
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(events);

    private static ApprovedEventView Approved(string title) => new(
        Id: Guid.NewGuid(), CampId: Guid.NewGuid(), GuideSharedVenueId: null, SubmitterUserId: Guid.NewGuid(),
        CategoryId: Guid.NewGuid(), CategorySlug: "music", CategoryName: "Music", CategoryIsSensitive: false,
        VenueName: null, Title: title, Description: string.Empty, LocationNote: null, Host: null,
        StartAt: Instant.FromUtc(2026, 8, 1, 10, 0), DurationMinutes: 60, IsRecurring: false, RecurrenceDays: null,
        PriorityRank: 0, SubmittedAt: Instant.FromUtc(2026, 8, 1, 10, 0), LastUpdatedAt: Instant.FromUtc(2026, 8, 1, 10, 0));
}
