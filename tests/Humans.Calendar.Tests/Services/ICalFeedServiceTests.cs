using AwesomeAssertions;
using System.Globalization;
using Humans.Base.Extensions;
using Humans.Calendar.Contracts;
using Humans.Calendar.Services;
using Microsoft.Extensions.Logging;
using Xunit;
using NodaTime;
using NSubstitute;
using Humans.Users.Contracts;
using Humans.Base.Configuration;
using Microsoft.Extensions.Options;

namespace Humans.Calendar.Tests.Services;

public class ICalFeedServiceTests
{
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly ILogger<ICalFeedService> _logger = Substitute.For<ILogger<ICalFeedService>>();
    private readonly ICalendarFeedTokenService _tokens = Substitute.For<ICalendarFeedTokenService>();

    private ICalFeedService CreateService(params ICalendarFeedContributor[] contributors) =>
        new(_users, _tokens, contributors,
            Options.Create(new EmailSettings { BaseUrl = "https://calendar.example" }),
            _logger);

    private static CalendarFeedItem MakeItem(string uid, string source, Instant start) =>
        new(
            Uid: uid,
            Source: source,
            Summary: $"Summary {uid}",
            Description: null,
            Start: start,
            End: start.Plus(Duration.FromHours(2)),
            Location: null,
            Url: "/Shifts/Mine");

    /// <summary>
    /// Stubs the read the way Users answers it: <paramref name="requestedId"/> is the id
    /// the caller asks for, and the row that comes back is <paramref name="resolvedId"/>'s — the
    /// same row for a live account, the survivor's for a merge tombstone.
    /// </summary>
    private void StubUser(Guid requestedId, Guid? icalToken, Guid? resolvedId = null, string language = "en")
    {
        var user = new User
        {
            Id = resolvedId ?? requestedId,
            DisplayName = "Test Human",
            PreferredLanguage = language,
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        };

        // The token is Calendar's own row, keyed by the id asked for — not a field
        // that travels with the merge-resolved user row.
        _tokens.GetAsync(requestedId, Arg.Any<CancellationToken>()).Returns(icalToken);
        var info = UserInfo.Create(
            user: user,
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: null,
            communicationPreferences: []);
        _users.GetUserInfoAsync(requestedId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(info));
    }

    [HumansFact]
    public async Task GetFeedItemsAsync_MergesContributors()
    {
        var early = Instant.FromUtc(2026, 7, 1, 10, 0);
        var late = Instant.FromUtc(2026, 7, 3, 10, 0);
        var service = CreateService(
            new FakeContributor(MakeItem("b@x", "Shifts", late)),
            new FakeContributor(MakeItem("a@x", "Events", early)));

        var items = await service.GetFeedItemsAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        items.Should().HaveCount(2);
        items.Select(i => i.Uid).Should().BeEquivalentTo(["a@x", "b@x"]);
    }

    [HumansTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GetFeedItemsAsync_PropagatesFailuresWithoutLoggingAbortedRequests(
        bool dependencyCancelled, bool callerCancelled)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        if (callerCancelled) await cancellation.CancelAsync();
        Exception exception = dependencyCancelled
            ? new OperationCanceledException(cancellation.Token)
            : new InvalidOperationException("boom");
        var service = CreateService(new FakeContributor(exception));
        var act = () => service.GetFeedItemsAsync(Guid.NewGuid(), cancellation.Token);

        if (dependencyCancelled)
            (await act.Should().ThrowAsync<OperationCanceledException>()).Which.Should().BeSameAs(exception);
        else
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        var errors = _logger.ReceivedCalls().Where(call => call.GetArguments()[0] is LogLevel.Error).ToList();
        errors.Should().HaveCount(callerCancelled ? 0 : 1);
        if (!callerCancelled) errors.Single().GetArguments()[3].Should().BeSameAs(exception);
    }

    [HumansFact]
    public async Task GetFeedIcsAsync_UnknownUser_ReturnsNull()
    {
        _users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>((UserInfo?)null));
        var service = CreateService();

        var ics = await service.GetFeedIcsAsync(Guid.NewGuid(), Guid.NewGuid(), TestContext.Current.CancellationToken);

        ics.Should().BeNull();
    }

    [HumansFact]
    public async Task GetFeedIcsAsync_NoTokenOnUser_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        StubUser(userId, icalToken: null);
        var service = CreateService();

        var ics = await service.GetFeedIcsAsync(userId, Guid.NewGuid(), TestContext.Current.CancellationToken);

        ics.Should().BeNull();
    }

    [HumansFact]
    public async Task GetFeedIcsAsync_WrongToken_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        StubUser(userId, icalToken: Guid.NewGuid());
        var service = CreateService();

        var ics = await service.GetFeedIcsAsync(userId, Guid.NewGuid(), TestContext.Current.CancellationToken);

        ics.Should().BeNull();
    }

    [HumansFact]
    public async Task GetFeedIcsAsync_MergedUser_ReturnsNull()
    {
        var tombstoneId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var tombstoneToken = Guid.NewGuid();
        // The read resolves the tombstone forward, so the row that comes back is the
        // survivor's — the URL minted for the merged-away account stops working.
        StubUser(tombstoneId, icalToken: Guid.NewGuid(), resolvedId: survivorId);
        var service = CreateService();

        var ics = await service.GetFeedIcsAsync(tombstoneId, tombstoneToken, TestContext.Current.CancellationToken);

        ics.Should().BeNull();
    }

    [HumansFact]
    public async Task GetFeedIcsAsync_MergedUser_SurvivorsOwnToken_ReturnsNull()
    {
        var tombstoneId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var survivorToken = Guid.NewGuid();
        // Same resolution, but now the token presented is the survivor's own, so the token
        // check alone would pass. The feed is still a 404: serving it would answer the old
        // URL under an id that no longer names a human, and tell whoever holds that URL
        // which account absorbed it.
        StubUser(tombstoneId, icalToken: survivorToken, resolvedId: survivorId);
        var service = CreateService();

        var ics = await service.GetFeedIcsAsync(tombstoneId, survivorToken, TestContext.Current.CancellationToken);

        ics.Should().BeNull();
    }

    [HumansTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("fr")]
    [InlineData("ca")]
    public async Task GetFeedIcsAsync_ValidToken_SerializesItemsAsUtcVEvents(string language)
    {
        var userId = Guid.NewGuid();
        var token = Guid.NewGuid();
        using var callerCulture = new CultureScope("en");
        StubUser(userId, icalToken: token, language: language);
        var contributor = Substitute.For<ICalendarFeedContributor>();
        contributor.GetCalendarItemsForUserAsync(userId, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await Task.Yield();
            return [
                MakeItem("later@humans.nobodies.team", "Events", Instant.FromUtc(2026, 7, 3, 8, 0)),
                MakeItem("shift-1@humans.nobodies.team", "Shifts", Instant.FromUtc(2026, 7, 2, 8, 0))
                    with { Summary = $"Owner language {CultureInfo.CurrentUICulture.Name}" },
            ];
        });
        var service = CreateService(contributor);

        var ics = await service.GetFeedIcsAsync(userId, token, TestContext.Current.CancellationToken);

        ics.Should().NotBeNull();
        ics.Should().Contain("BEGIN:VCALENDAR");
        ics.Should().Contain($"SUMMARY:Owner language {language}");
        CultureInfo.CurrentCulture.Name.Should().Be("en");
        CultureInfo.CurrentUICulture.Name.Should().Be("en");
        ics.Should().Contain("UID:shift-1@humans.nobodies.team");
        ics.Should().Contain("DTSTART:20260702T080000Z");
        ics.Should().Contain("DTEND:20260702T100000Z");
        ics.Should().Contain("CATEGORIES:Shifts");
        ics.Should().Contain("URL:https://calendar.example/Shifts/Mine");
        ics.Should().Contain("X-WR-CALNAME:Nobodies");

        // Round-trips through the same library calendar clients use.
        var parsed = Ical.Net.Calendar.Load(ics);
        parsed!.Events.Select(e => e.Uid).Should().Equal("shift-1@humans.nobodies.team", "later@humans.nobodies.team");
    }

    [HumansFact]
    public async Task GetFeedIcsAsync_NoItems_StillValidCalendar()
    {
        var userId = Guid.NewGuid();
        var token = Guid.NewGuid();
        StubUser(userId, icalToken: token);
        var service = CreateService(new FakeContributor());

        var ics = await service.GetFeedIcsAsync(userId, token, TestContext.Current.CancellationToken);

        ics.Should().NotBeNull();
        var parsed = Ical.Net.Calendar.Load(ics);
        parsed!.Events.Should().BeEmpty();
    }

    private sealed class FakeContributor : ICalendarFeedContributor
    {
        private readonly CalendarFeedItem[] _items;
        private readonly Exception? _throw;

        public FakeContributor(params CalendarFeedItem[] items) => _items = items;

        public FakeContributor(Exception throwOnCall)
        {
            _items = [];
            _throw = throwOnCall;
        }

        public Task<IReadOnlyList<CalendarFeedItem>> GetCalendarItemsForUserAsync(Guid userId, CancellationToken ct)
        {
            if (_throw is not null) throw _throw;
            return Task.FromResult<IReadOnlyList<CalendarFeedItem>>(_items);
        }

        public Task<IReadOnlyList<CalendarFeedItem>> GetPublicItemsForWindowAsync(Instant from, Instant to, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CalendarFeedItem>>([]);
    }
}
