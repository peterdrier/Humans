using AwesomeAssertions;
using Humans.Holded.Contracts;
using Humans.Holded.Data;
using Humans.Holded.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
    private readonly CapturingLogger<Service> _logger = new();

    public HoldedBankMovementSyncTests()
    {
        var options = new DbContextOptionsBuilder<HoldedDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var callLog = Substitute.For<IHoldedCallLog>();
        callLog.DrainAll().Returns([]);
        _service = new Service(new Repository(new TestDbContextFactory<HoldedDbContext>(options)), _client, callLog,
            new FakeClock(FixedNow), Options.Create(new HoldedSectionOptions()), _logger, FixedOrgTimeZone.Madrid);
    }

    private static HoldedBankMovementDto Line(string id, int day, string status = "pending") =>
        new() { Id = id, Date = new LocalDate(2026, 8, day), Amount = -10m, Status = status };

    private void Feed(params HoldedBankMovementDto[] lines) =>
        _client.ListBankMovementsAsync(
                "t1", Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>(),
                Arg.Any<IReadOnlySet<string>?>())
            .Returns(lines.ToList());

    [HumansTheory]
    [InlineData(BankSyncFailure.Transient)]
    [InlineData(BankSyncFailure.Permanent)]
    [InlineData(BankSyncFailure.Fault)]
    [InlineData(BankSyncFailure.Shutdown)]
    [InlineData(BankSyncFailure.OtherCancellation)]
    public async Task Sync_ClassifiesFailures_AndPreservesTheOriginalException(BankSyncFailure kind)
    {
        using var request = new CancellationTokenSource();
        Exception failure = kind switch
        {
            BankSyncFailure.Transient => new HoldedTransientException("Bank feed unavailable"),
            BankSyncFailure.Permanent => new HoldedPermanentException(403, null, "Bank feed forbidden"),
            BankSyncFailure.Shutdown => new OperationCanceledException(request.Token),
            BankSyncFailure.OtherCancellation => new OperationCanceledException("Dependency canceled"),
            _ => new IOException("Bank mirror unavailable"),
        };
        async Task<IReadOnlyList<HoldedBankMovementDto>> FailAsync()
        {
            if (kind is BankSyncFailure.Shutdown) await request.CancelAsync();
            throw failure;
        }
        _client.ListBankMovementsAsync("t1", Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), request.Token,
            Arg.Any<IReadOnlySet<string>?>()).Returns(_ => FailAsync());
        var sync = () => _service.SyncBankMovementsAsync("t1", new LocalDate(2026, 8, 1),
            new LocalDate(2026, 8, 9), ct: request.Token);

        (await Record.ExceptionAsync(sync)).Should().BeSameAs(failure);
        var log = _logger.Entries.Should().ContainSingle().Subject;
        var expected = kind is BankSyncFailure.Transient or BankSyncFailure.Permanent or BankSyncFailure.Shutdown;
        log.Level.Should().Be(expected ? LogLevel.Warning : LogLevel.Error);
        log.Exception.Should().BeSameAs(expected ? null : failure);
        if (kind is BankSyncFailure.Transient or BankSyncFailure.Permanent) log.Message.Should().Contain(failure.Message);
    }

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

public enum BankSyncFailure { Transient, Permanent, Fault, Shutdown, OtherCancellation }
