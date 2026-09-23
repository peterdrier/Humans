using AwesomeAssertions;
using Humans.Calendar.Contracts;
using Humans.Calendar.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Humans.Users.Contracts;
using Humans.Base.Configuration;
using Microsoft.Extensions.Options;

namespace Humans.Calendar.Tests.Services;

public class ICalFeedServiceTests
{
    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();
    private readonly ICalendarFeedTokenService _tokens = Substitute.For<ICalendarFeedTokenService>();

    private ICalFeedService CreateService(params ICalendarFeedContributor[] contributors) =>
        new(_users, _tokens, contributors,
            Options.Create(new EmailSettings { BaseUrl = "https://calendar.example" }),
            NullLogger<ICalFeedService>.Instance);

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
    /// Stubs the read the way Users answers it (#1704): <paramref name="requestedId"/> is the id
    /// the caller asks for, and the row that comes back is <paramref name="resolvedId"/>'s — the
    /// same row for a live account, the survivor's for a merge tombstone.
    /// </summary>
    private void StubUser(Guid requestedId, Guid? icalToken, Guid? resolvedId = null)
    {
        var user = new User
        {
            Id = resolvedId ?? requestedId,
            DisplayName = "Test Human",
            PreferredLanguage = "en",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        };

        // The token is Calendar's own row now, keyed by the id asked for — not a field
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
    public async Task GetFeedItemsAsync_MergesAndSortsByStart()
    {
        var early = Instant.FromUtc(2026, 7, 1, 10, 0);
        var late = Instant.FromUtc(2026, 7, 3, 10, 0);
        var service = CreateService(
            new FakeContributor(MakeItem("b@x", "Shifts", late)),
            new FakeContributor(MakeItem("a@x", "Events", early)));

        var items = await service.GetFeedItemsAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        items.Should().HaveCount(2);
        items[0].Uid.Should().Be("a@x");
        items[1].Uid.Should().Be("b@x");
    }

    [HumansFact]
    public async Task GetFeedItemsAsync_PropagatesContributorFailure()
    {
        var service = CreateService(new FakeContributor(new InvalidOperationException("boom")));

        var act = async () => await service.GetFeedItemsAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }

    [HumansFact]
    public async Task GetFeedIcsAsync_UnknownUser_ReturnsNull()
    {
        _users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>((UserInfo?)null));
        var service = CreateService();

        var ics = await service.GetFeedIcsAsync(Guid.NewGuid(), Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        ics.Should().BeNull();
    }

    [HumansFact]
    public async Task GetFeedIcsAsync_NoTokenOnUser_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        StubUser(userId, icalToken: null);
        var service = CreateService();

        var ics = await service.GetFeedIcsAsync(userId, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        ics.Should().BeNull();
    }

    [HumansFact]
    public async Task GetFeedIcsAsync_WrongToken_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        StubUser(userId, icalToken: Guid.NewGuid());
        var service = CreateService();

        var ics = await service.GetFeedIcsAsync(userId, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

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

        var ics = await service.GetFeedIcsAsync(tombstoneId, tombstoneToken, Xunit.TestContext.Current.CancellationToken);

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

        var ics = await service.GetFeedIcsAsync(tombstoneId, survivorToken, Xunit.TestContext.Current.CancellationToken);

        ics.Should().BeNull();
    }

    [HumansFact]
    public async Task GetFeedIcsAsync_ValidToken_SerializesItemsAsUtcVEvents()
    {
        var userId = Guid.NewGuid();
        var token = Guid.NewGuid();
        StubUser(userId, icalToken: token);
        var service = CreateService(new FakeContributor(
            MakeItem("shift-1@humans.nobodies.team", "Shifts", Instant.FromUtc(2026, 7, 2, 8, 0))));

        var ics = await service.GetFeedIcsAsync(userId, token, Xunit.TestContext.Current.CancellationToken);

        ics.Should().NotBeNull();
        ics.Should().Contain("BEGIN:VCALENDAR");
        ics.Should().Contain("UID:shift-1@humans.nobodies.team");
        ics.Should().Contain("DTSTART:20260702T080000Z");
        ics.Should().Contain("DTEND:20260702T100000Z");
        ics.Should().Contain("CATEGORIES:Shifts");
        ics.Should().Contain("URL:https://calendar.example/Shifts/Mine");
        ics.Should().Contain("X-WR-CALNAME:Nobodies");

        // Round-trips through the same library calendar clients use.
        var parsed = Ical.Net.Calendar.Load(ics!);
        parsed!.Events.Should().HaveCount(1);
    }

    [HumansFact]
    public async Task GetFeedIcsAsync_NoItems_StillValidCalendar()
    {
        var userId = Guid.NewGuid();
        var token = Guid.NewGuid();
        StubUser(userId, icalToken: token);
        var service = CreateService(new FakeContributor());

        var ics = await service.GetFeedIcsAsync(userId, token, Xunit.TestContext.Current.CancellationToken);

        ics.Should().NotBeNull();
        var parsed = Ical.Net.Calendar.Load(ics!);
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
