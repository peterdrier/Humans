using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Interfaces.Events;
using Humans.Web.Models.Events;
using Humans.Web.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.ViewComponents;

/// <summary>
/// Covers <see cref="CampEventsViewComponent"/>: it renders a camp's approved
/// events ordered by start time with the viewer's favourites marked, and renders
/// nothing when the camp has no approved events or the viewer is unauthenticated.
/// </summary>
public class CampEventsViewComponentTests
{
    private readonly IEventServiceRead _events = Substitute.For<IEventServiceRead>();

    private CampEventsViewComponent BuildSut(Guid? userId, bool eventsEnabled = true)
    {
        var identity = userId is null
            ? new ClaimsIdentity()
            : new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString())], "Test");
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { ["Features:Events"] = eventsEnabled ? "true" : "false" })
            .Build();
        return new CampEventsViewComponent(_events, config, NullLogger<CampEventsViewComponent>.Instance)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = http },
            },
        };
    }

    private static ApprovedEventView Approved(Guid id, string title, Instant startAt, string description = "") => new(
        Id: id, CampId: Guid.NewGuid(), GuideSharedVenueId: null, SubmitterUserId: Guid.NewGuid(),
        CategoryId: Guid.NewGuid(), CategorySlug: "music", CategoryName: "Music", CategoryIsSensitive: false,
        VenueName: null, Title: title, Description: description, LocationNote: null, Host: null,
        StartAt: startAt, DurationMinutes: 60, IsRecurring: false, RecurrenceDays: null,
        PriorityRank: 0, SubmittedAt: startAt, LastUpdatedAt: startAt);

    private void StubSettings() =>
        _events.GetGuideSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new EventGuideSettingsView(
                Id: Guid.NewGuid(), EventSettingsId: Guid.NewGuid(),
                SubmissionOpenAt: Instant.MinValue, SubmissionCloseAt: Instant.MaxValue,
                GuidePublishAt: Instant.MaxValue, MaxPrintSlots: 0, TimeZoneId: "Europe/Madrid",
                CreatedAt: Instant.MinValue, UpdatedAt: Instant.MinValue));

    [HumansFact]
    public async Task Renders_OrderedRows_WithFavouritesMarked()
    {
        var userId = Guid.NewGuid();
        var campId = Guid.NewGuid();
        var early = Approved(Guid.NewGuid(), "Early", Instant.FromUtc(2026, 8, 1, 10, 0), description: "Morning yoga by the temple.");
        var late = Approved(Guid.NewGuid(), "Late", Instant.FromUtc(2026, 8, 1, 22, 0));
        _events.GetApprovedEventsAsync(campId, null, null, null,
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns([late, early]); // returned out of order on purpose
        StubSettings();
        _events.GetFavouriteEventIdsAsync(userId, Arg.Any<CancellationToken>())
            .Returns([early.Id]);

        var result = await BuildSut(userId).InvokeAsync(campId, "shenanicamp");

        var view = result.Should().BeOfType<ViewViewComponentResult>().Subject;
        var vm = view.ViewData!.Model.Should().BeOfType<CampEventsCardViewModel>().Subject;
        vm.CampSlug.Should().Be("shenanicamp");
        vm.Rows.Select(r => r.Title).Should().ContainInOrder("Early", "Late");
        vm.Rows[0].IsFavourited.Should().BeTrue();   // Early — favourited
        vm.Rows[1].IsFavourited.Should().BeFalse();  // Late — not favourited
        vm.Rows[0].Description.Should().Be("Morning yoga by the temple.");
    }

    [HumansFact]
    public async Task RendersNothing_WhenNoApprovedEvents()
    {
        var userId = Guid.NewGuid();
        var campId = Guid.NewGuid();
        _events.GetApprovedEventsAsync(campId, null, null, null,
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await BuildSut(userId).InvokeAsync(campId, "shenanicamp");

        result.Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
        await _events.DidNotReceive().GetFavouriteEventIdsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RendersNothing_WhenEventsFeatureDisabled()
    {
        var result = await BuildSut(Guid.NewGuid(), eventsEnabled: false)
            .InvokeAsync(Guid.NewGuid(), "shenanicamp");

        result.Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
        await _events.DidNotReceive().GetApprovedEventsAsync(
            Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RendersNothing_WhenUnauthenticated()
    {
        var result = await BuildSut(userId: null).InvokeAsync(Guid.NewGuid(), "shenanicamp");

        result.Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
        await _events.DidNotReceive().GetApprovedEventsAsync(
            Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }
}
