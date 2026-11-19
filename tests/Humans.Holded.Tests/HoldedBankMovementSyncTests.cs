using AwesomeAssertions;
using Humans.Holded.Contracts;
using Humans.Holded.Data;
using Humans.Holded.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Holded.Tests;

[Collection("HoldedMirrorSyncGates")]
public sealed class HoldedBankMovementSyncTests
{
    private static readonly Instant FixedNow = Instant.FromUtc(2026, 8, 10, 12, 0);
    private readonly IHoldedClient _client = Substitute.For<IHoldedClient>();
    private readonly Service _service;

    public HoldedBankMovementSyncTests()
    {
        var options = new DbContextOptionsBuilder<HoldedDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var callLog = Substitute.For<IHoldedCallLog>();
        callLog.DrainAll().Returns([]);
        _service = new Service(new Repository(new TestDbContextFactory<HoldedDbContext>(options)), _client, callLog,
            new FakeClock(FixedNow), Options.Create(new HoldedSectionOptions()), NullLogger<Service>.Instance);
    }

    private static HoldedBankMovementDto Line(string id, int day, string status = "pending") =>
        new() { Id = id, Date = new LocalDate(2026, 8, day), Amount = -10m, Status = status };

    private void Feed(params HoldedBankMovementDto[] lines) =>
        _client.ListBankMovementsAsync(
                "t1", Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>(),
                Arg.Any<IReadOnlySet<string>?>())
            .Returns(lines.ToList());

    [HumansFact]
    public async Task Sync_ReplacesOnlyItsWindow_AndStampsTheLastSync()
    {
        var ct = TestContext.Current.CancellationToken;
        Feed(Line("a", 1), Line("b", 5));
        await _service.SyncBankMovementsAsync("t1", new LocalDate(2026, 8, 1), new LocalDate(2026, 8, 9), ct: ct);

        // Each sync replaces only its own window: "a" outlives the 5-9 sync and goes with the 1-4 one.
        Feed(Line("b", 5, "reconciled"));
        (await _service.SyncBankMovementsAsync("t1", new LocalDate(2026, 8, 5), new LocalDate(2026, 8, 9), ct: ct))
            .Should().BeTrue();
        Feed(Line("c", 6));
        await _service.SyncBankMovementsAsync("t1", new LocalDate(2026, 8, 1), new LocalDate(2026, 8, 4), ct: ct);

        var mirror = await _service.GetBankMovementsAsync("t1", new LocalDate(2026, 8, 1), new LocalDate(2026, 8, 9), ct);
        mirror.LastSyncAt.Should().Be(FixedNow);
        mirror.Movements.Select(m => (m.Id, m.Status)).Should().BeEquivalentTo([("b", "reconciled"), ("c", "pending")]);
    }
}
