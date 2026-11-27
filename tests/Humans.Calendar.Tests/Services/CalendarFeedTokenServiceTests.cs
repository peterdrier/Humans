using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using NSubstitute;
using Humans.Calendar.Data;
using Humans.Calendar.Domain;
using Humans.Calendar.Services;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Calendar.Tests.Services;

/// <summary>
/// The feed credential's whole lifecycle: mint on first
/// ask, reuse after, rotate on demand, and the two user-lifecycle fan-outs the table obliges
/// the section to join.
/// </summary>
public sealed class CalendarFeedTokenServiceTests : IDisposable
{
    private readonly CalendarDbContext _db;
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly CalendarFeedTokenService _sut;
    private readonly Guid _user = Guid.NewGuid();

    public CalendarFeedTokenServiceTests()
    {
        var options = new DbContextOptionsBuilder<CalendarDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new CalendarDbContext(options);
        _sut = new CalendarFeedTokenService(
            new CalendarRepository(new TestDbContextFactory<CalendarDbContext>(options)), _audit);
    }

    private CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [HumansFact]
    public async Task Get_NeverOpenedCard_HasNoToken()
    {
        (await _sut.GetAsync(_user, Ct)).Should().BeNull();
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task Ensure_CalledTwice_ReturnsSameToken()
    {
        var first = await _sut.EnsureAsync(_user, Ct);
        var second = await _sut.EnsureAsync(_user, Ct);

        first.Should().NotBeEmpty();
        second.Should().Be(first);
        (await _sut.GetAsync(_user, Ct)).Should().Be(first);
        var audit = _audit.ReceivedCalls().Should().ContainSingle().Subject.GetArguments();
        audit[1].Should().Be("User");
        audit[2].Should().Be(_user);
        audit[0].Should().Be(CalendarAuditActions.CalendarFeedTokenCreated);
        audit[3].Should().Be("Personal calendar feed token created");
        audit[4].Should().Be(_user);
        audit[3]!.ToString().Should().NotContain(first.ToString());
    }

    [HumansFact]
    public async Task Ensure_TokenWrittenMeanwhile_IsAdopted()
    {
        // The row appears without the service putting it there — a concurrent first view
        // of /Calendar, or any other writer. Ensure must adopt it: minting over a live
        // token would silently revoke a subscription the member is already using.
        var winner = Guid.NewGuid();
        _db.CalendarFeedTokens.Add(new CalendarFeedToken { UserId = _user, Token = winner });
        await _db.SaveChangesAsync(Ct);

        var got = await _sut.EnsureAsync(_user, Ct);

        got.Should().Be(winner, "adopting the existing token beats revoking a live feed");
        (await _db.CalendarFeedTokens.CountAsync(t => t.UserId == _user, Xunit.TestContext.Current.CancellationToken)).Should().Be(1);
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task Rotate_ExistingToken_LeavesOneRow()
    {
        var original = await _sut.EnsureAsync(_user, Ct);
        _audit.ClearReceivedCalls();

        var rotated = await _sut.RotateAsync(_user, Ct);

        rotated.Should().NotBe(original).And.NotBe(Guid.Empty);
        var audit = _audit.ReceivedCalls().Should().ContainSingle().Subject.GetArguments();
        audit[1].Should().Be("User");
        audit[2].Should().Be(_user);
        audit[0].Should().Be(CalendarAuditActions.CalendarFeedTokenRotated);
        audit[3].Should().Be("Personal calendar feed token rotated");
        audit[4].Should().Be(_user);
        audit[3]!.ToString().Should().NotContain(original.ToString()).And.NotContain(rotated.ToString());
        (await _sut.GetAsync(_user, Ct)).Should().Be(rotated);
        (await _db.CalendarFeedTokens.CountAsync(t => t.UserId == _user, Xunit.TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [HumansFact]
    public async Task Rotate_OtherMembersToken_IsUntouched()
    {
        var other = Guid.NewGuid();
        var othersToken = await _sut.EnsureAsync(other, Ct);
        await _sut.EnsureAsync(_user, Ct);

        await _sut.RotateAsync(_user, Ct);

        (await _sut.GetAsync(other, Ct)).Should().Be(othersToken);
    }

    [HumansFact]
    public async Task Erasure_Retried_DeletesToken()
    {
        await _sut.EnsureAsync(_user, Ct);

        await _sut.EraseForUserAsync(_user, Ct);
        await _sut.EraseForUserAsync(_user, Ct);

        (await _sut.GetAsync(_user, Ct)).Should().BeNull();
    }

    [HumansFact]
    public async Task Merge_EliminatedAccount_LosesFeed()
    {
        var survivor = Guid.NewGuid();
        var survivorToken = await _sut.EnsureAsync(survivor, Ct);
        await _sut.EnsureAsync(_user, Ct);

        await _sut.ReassignAsync(_user, survivor, Guid.NewGuid(), Instant.MinValue, Ct);

        (await _sut.GetAsync(_user, Ct)).Should().BeNull();
        (await _sut.GetAsync(survivor, Ct)).Should().Be(survivorToken);
    }

    [HumansFact]
    public async Task Export_FeedExists_OmitsToken()
    {
        var token = await _sut.EnsureAsync(_user, Ct);

        var slices = await _sut.ContributeForUserAsync(_user, Ct);

        var slice = slices.Should().ContainSingle().Subject;
        slice.SectionName.Should().Be(CalendarFeedTokenService.CalendarFeedToken);
        System.Text.Json.JsonSerializer.Serialize(slice.Data)
            .Should().Contain("true").And.NotContain(token.ToString());
    }

    [HumansFact]
    public async Task Export_NoFeed_ReportsNone()
    {
        var slices = await _sut.ContributeForUserAsync(_user, Ct);

        System.Text.Json.JsonSerializer.Serialize(slices.Single().Data).Should().Contain("false");
    }

    public void Dispose() => _db.Dispose();
}
