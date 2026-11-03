using Humans.Users.Contracts;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Events.Contracts;
using Humans.Events.Models;
using Humans.Events.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Events.Tests.ViewComponents;

/// <summary>
/// Covers <see cref="EventsCardViewComponent"/>: it renders the approved events
/// for exactly one scope — a camp, or a user's personal (non-camp) submissions —
/// ordered by start time with the viewer's favourites marked, and renders nothing
/// when the scope has no approved events or the viewer is unauthenticated.
/// </summary>
public class EventsCardViewComponentTests
{
    private readonly IEventService _events = Substitute.For<IEventService>();
    private readonly IUserService _users = Substitute.For<IUserService>();

    private EventsCardViewComponent BuildSut(
        Guid? viewerId, bool eventsEnabled = true, CancellationToken requestAborted = default)
    {
        if (viewerId is { } knownViewer)
            _users.GetUserInfoAsync(knownViewer, Arg.Any<CancellationToken>())
                .Returns(UserInfo.Create(new User { Id = knownViewer }, [], [], null));
        var identity = viewerId is null
            ? new ClaimsIdentity()
            : new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, viewerId.Value.ToString())], "Test");
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(identity), RequestAborted = requestAborted };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { ["Features:Events"] = eventsEnabled ? "true" : "false" })
            .Build();
        return new EventsCardViewComponent(_events, config, _users, NullLogger<EventsCardViewComponent>.Instance)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = http },
            },
        };
    }

    [HumansTheory]
    [Xunit.InlineData(AccountScenario.Merged)]
    [Xunit.InlineData(AccountScenario.Missing)]
    [Xunit.InlineData(AccountScenario.Cancelled)]
    public async Task Favourite_viewer_resolves_before_loading_the_card(AccountScenario account)
    {
        var cookieId = Guid.NewGuid();
        var survivor = Guid.NewGuid();
        var campId = Guid.NewGuid();
        using var request = new CancellationTokenSource();
        var component = BuildSut(cookieId, requestAborted: request.Token);
        _users.GetUserInfoAsync(cookieId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().Should().Be(request.Token);
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return new ValueTask<UserInfo?>(account == AccountScenario.Missing ? null :
                UserInfo.Create(new User { Id = survivor }, [], [], null));
        });
        var ev = Approved(Guid.NewGuid(), "Event", Instant.FromUtc(2026, 8, 1, 10, 0));
        _events.GetApprovedEventsAsync(campId, null, null, null, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns([ev]);
        _events.GetFavouriteEventIdsAsync(survivor, request.Token).Returns(new HashSet<Guid> { ev.Id });
        if (account == AccountScenario.Cancelled)
            await request.CancelAsync();
        var invoke = () => component.InvokeAsync(campId: campId);

        if (account == AccountScenario.Cancelled)
            await invoke.Should().ThrowAsync<OperationCanceledException>();
        else if (account == AccountScenario.Missing)
            (await invoke()).Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
        else
        {
            var model = (await invoke()).Should().BeOfType<ViewViewComponentResult>().Which.ViewData!.Model
                .Should().BeOfType<EventsCardViewModel>().Subject;
            model.Rows.Should().ContainSingle().Which.IsFavourited.Should().BeTrue();
            await _events.Received(1).GetFavouriteEventIdsAsync(survivor, request.Token);
            return;
        }
        _events.ReceivedCalls().Should().BeEmpty();
    }

    private static ApprovedEventView Approved(Guid id, string title, Instant startAt, string description = "", Guid? submitterUserId = null, bool isPersonal = false) => new(
        Id: id, CampId: isPersonal ? null : Guid.NewGuid(), GuideSharedVenueId: isPersonal ? Guid.NewGuid() : null, SubmitterUserId: submitterUserId ?? Guid.NewGuid(),
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
    public async Task CampScope_Renders_OrderedRows_WithFavouritesMarked()
    {
        var viewerId = Guid.NewGuid();
        var campId = Guid.NewGuid();
        var early = Approved(Guid.NewGuid(), "Early", Instant.FromUtc(2026, 8, 1, 10, 0), description: "Morning yoga by the temple.");
        var late = Approved(Guid.NewGuid(), "Late", Instant.FromUtc(2026, 8, 1, 22, 0));
        _events.GetApprovedEventsAsync(campId, null, null, null,
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns([late, early]); // returned out of order on purpose
        StubSettings();
        _events.GetFavouriteEventIdsAsync(viewerId, Arg.Any<CancellationToken>())
            .Returns([early.Id]);

        var result = await BuildSut(viewerId).InvokeAsync(campId: campId);

        var view = result.Should().BeOfType<ViewViewComponentResult>().Subject;
        var vm = view.ViewData!.Model.Should().BeOfType<EventsCardViewModel>().Subject;
        vm.Rows.Select(r => r.Title).Should().ContainInOrder("Early", "Late");
        vm.Rows[0].IsFavourited.Should().BeTrue();   // Early — favourited
        vm.Rows[1].IsFavourited.Should().BeFalse();  // Late — not favourited
        vm.Rows[0].Description.Should().Be("Morning yoga by the temple.");
    }

    [HumansFact]
    public async Task UserScope_FiltersToSubmittersPersonalEvents()
    {
        var viewerId = Guid.NewGuid();
        var susieId = Guid.NewGuid();
        var susies = Approved(Guid.NewGuid(), "Susie's Salon", Instant.FromUtc(2026, 8, 1, 10, 0), submitterUserId: susieId, isPersonal: true);
        var susiesCampEvent = Approved(Guid.NewGuid(), "Susie's Camp Bash", Instant.FromUtc(2026, 8, 1, 11, 0), submitterUserId: susieId);
        var other = Approved(Guid.NewGuid(), "Someone Else's", Instant.FromUtc(2026, 8, 1, 12, 0), isPersonal: true);
        _events.GetApprovedEventsAsync(null, null, null, null,
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns([susies, susiesCampEvent, other]);
        StubSettings();
        _events.GetFavouriteEventIdsAsync(viewerId, Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await BuildSut(viewerId).InvokeAsync(userId: susieId);

        var view = result.Should().BeOfType<ViewViewComponentResult>().Subject;
        var vm = view.ViewData!.Model.Should().BeOfType<EventsCardViewModel>().Subject;
        vm.Rows.Select(r => r.Title).Should().Equal("Susie's Salon");
    }

    [HumansFact]
    public async Task RendersNothing_WhenNoApprovedEvents()
    {
        var viewerId = Guid.NewGuid();
        var campId = Guid.NewGuid();
        _events.GetApprovedEventsAsync(campId, null, null, null,
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await BuildSut(viewerId).InvokeAsync(campId: campId);

        result.Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
        await _events.DidNotReceive().GetFavouriteEventIdsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RendersNothing_WhenEventsFeatureDisabled()
    {
        var result = await BuildSut(Guid.NewGuid(), eventsEnabled: false)
            .InvokeAsync(campId: Guid.NewGuid());

        result.Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
        await _events.DidNotReceive().GetApprovedEventsAsync(
            Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RequestAborted_PropagatesCancellation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        _events.GetApprovedEventsAsync(Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
                Arg.Any<IReadOnlyList<string>>(), aborted.Token)
            .Returns(Task.FromException<IReadOnlyList<ApprovedEventView>>(
                new OperationCanceledException(aborted.Token)));

        var act = () => BuildSut(Guid.NewGuid(), requestAborted: aborted.Token)
            .InvokeAsync(campId: Guid.NewGuid());

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansFact]
    public async Task RendersNothing_WhenUnauthenticated()
    {
        var result = await BuildSut(viewerId: null).InvokeAsync(campId: Guid.NewGuid());

        result.Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
        await _events.DidNotReceive().GetApprovedEventsAsync(
            Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RendersNothing_WhenScopeIsNotExactlyOne()
    {
        var sut = BuildSut(Guid.NewGuid());

        (await sut.InvokeAsync())
            .Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
        (await sut.InvokeAsync(campId: Guid.NewGuid(), userId: Guid.NewGuid()))
            .Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
        await _events.DidNotReceive().GetApprovedEventsAsync(
            Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }
}
