using AwesomeAssertions;
using Humans.Calendar.Data;
using Humans.Calendar.Domain;
using Humans.Calendar.Services;
using Humans.Gdpr.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Calendar.Tests.Services;

/// <summary>
/// The feed credential's whole lifecycle, now that Calendar owns the table: mint on first
/// ask, reuse after, rotate on demand, and the two user-lifecycle fan-outs the table obliges
/// the section to join.
/// </summary>
public sealed class CalendarFeedTokenServiceTests : IDisposable
{
    private readonly CalendarDbContext _db;
    private readonly CalendarFeedTokenService _sut;
    private readonly Guid _user = Guid.NewGuid();

    public CalendarFeedTokenServiceTests()
    {
        var options = new DbContextOptionsBuilder<CalendarDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new CalendarDbContext(options);
        _sut = new CalendarFeedTokenService(
            new CalendarRepository(new TestDbContextFactory<CalendarDbContext>(options)));
    }

    private CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [HumansFact]
    public async Task A_member_who_never_opened_the_card_has_no_token()
    {
        (await _sut.GetAsync(_user, Ct)).Should().BeNull();
    }

    [HumansFact]
    public async Task Ensure_mints_once_and_then_returns_the_same_token()
    {
        var first = await _sut.EnsureAsync(_user, Ct);
        var second = await _sut.EnsureAsync(_user, Ct);

        first.Should().NotBeEmpty();
        second.Should().Be(first);
        (await _sut.GetAsync(_user, Ct)).Should().Be(first);
    }

    [HumansFact]
    public async Task Ensure_adopts_a_token_written_behind_its_back_instead_of_replacing_it()
    {
        // The row appears without the service putting it there — a concurrent first view
        // of /Calendar, or any other writer. Ensure must adopt it: minting over a live
        // token would silently revoke a subscription the member is already using.
        // (The DbUpdateException fallback for the narrower read-then-insert window is not
        // unit-tested, matching the repo's other racing inserts — CommunicationPreference
        // AddDefaultsOrReloadAsync and GateRepository's unique-violation catch.)
        var winner = Guid.NewGuid();
        _db.CalendarFeedTokens.Add(new CalendarFeedToken { UserId = _user, Token = winner });
        await _db.SaveChangesAsync(Ct);

        var got = await _sut.EnsureAsync(_user, Ct);

        got.Should().Be(winner, "adopting the existing token beats revoking a live feed");
        _db.CalendarFeedTokens.Count(t => t.UserId == _user).Should().Be(1);
    }

    [HumansFact]
    public async Task Rotate_replaces_the_token_and_leaves_exactly_one_row()
    {
        var original = await _sut.EnsureAsync(_user, Ct);

        var rotated = await _sut.RotateAsync(_user, Ct);

        rotated.Should().NotBe(original).And.NotBe(Guid.Empty);
        (await _sut.GetAsync(_user, Ct)).Should().Be(rotated);
        _db.CalendarFeedTokens.Count(t => t.UserId == _user).Should().Be(1);
    }

    [HumansFact]
    public async Task Rotate_touches_nobody_elses_token()
    {
        var other = Guid.NewGuid();
        var othersToken = await _sut.EnsureAsync(other, Ct);
        await _sut.EnsureAsync(_user, Ct);

        await _sut.RotateAsync(_user, Ct);

        (await _sut.GetAsync(other, Ct)).Should().Be(othersToken);
    }

    [HumansFact]
    public async Task Erasure_deletes_the_token_and_is_safe_to_retry()
    {
        await _sut.EnsureAsync(_user, Ct);

        await _sut.EraseForUserAsync(_user, Ct);
        await _sut.EraseForUserAsync(_user, Ct);

        (await _sut.GetAsync(_user, Ct)).Should().BeNull();
    }

    [HumansFact]
    public async Task Merge_kills_the_eliminated_accounts_feed_and_leaves_the_survivors_alone()
    {
        var survivor = Guid.NewGuid();
        var survivorToken = await _sut.EnsureAsync(survivor, Ct);
        await _sut.EnsureAsync(_user, Ct);

        await _sut.ReassignAsync(_user, survivor, Guid.NewGuid(), Instant.MinValue, Ct);

        (await _sut.GetAsync(_user, Ct)).Should().BeNull();
        (await _sut.GetAsync(survivor, Ct)).Should().Be(survivorToken);
    }

    [HumansFact]
    public async Task The_export_says_whether_a_feed_exists_and_never_carries_the_token()
    {
        var token = await _sut.EnsureAsync(_user, Ct);

        var slices = await _sut.ContributeForUserAsync(_user, Ct);

        var slice = slices.Should().ContainSingle().Subject;
        slice.SectionName.Should().Be(GdprExportSections.CalendarFeedToken);
        System.Text.Json.JsonSerializer.Serialize(slice.Data)
            .Should().Contain("true").And.NotContain(token.ToString());
    }

    [HumansFact]
    public async Task The_export_reports_no_feed_for_a_member_who_has_none()
    {
        var slices = await _sut.ContributeForUserAsync(_user, Ct);

        System.Text.Json.JsonSerializer.Serialize(slices.Single().Data).Should().Contain("false");
    }

    public void Dispose() => _db.Dispose();
}
