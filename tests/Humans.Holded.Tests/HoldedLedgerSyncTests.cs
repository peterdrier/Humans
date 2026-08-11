using AwesomeAssertions;
using Humans.Application.Interfaces.Holded;
using Humans.Holded.Data;
using Humans.Holded.Domain;
using Humans.Holded.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Holded.Tests;

public sealed class HoldedLedgerSyncTests
{
    private static readonly Instant FixedNow = Instant.FromUtc(2026, 8, 10, 12, 0);
    private static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
    private static readonly LocalDate Today = FixedNow.InZone(MadridZone).Date;

    private readonly Repository _repo;
    private readonly IHoldedClient _client = Substitute.For<IHoldedClient>();
    private readonly IHoldedCallLog _callLog = Substitute.For<IHoldedCallLog>();
    private readonly Service _service;

    public HoldedLedgerSyncTests()
    {
        var options = new DbContextOptionsBuilder<HoldedDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _repo = new Repository(new TestDbContextFactory<HoldedDbContext>(options));
        _callLog.DrainAll().Returns([]);
        _client.ListAccountingAccountsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<HoldedAccountDto>());
        _service = new Service(_repo, _client, _callLog, new FakeClock(FixedNow),
            Options.Create(new HoldedSectionOptions()), NullLogger<Service>.Instance);
    }

    private static HoldedLedgerLineDto Dto(int entry, int line, int account, Instant date,
        decimal debit = 0m, decimal credit = 0m) =>
        new()
        {
            EntryNumber = entry,
            Line = line,
            AccountNum = account,
            Date = date,
            Debit = debit,
            Credit = credit,
        };

    private static HoldedAccountDto Account(int number, decimal balance, bool archived = false) =>
        new()
        {
            Id = $"id-{number}",
            Number = number,
            Name = $"acct {number}",
            Debit = balance > 0 ? balance : 0m,
            Credit = balance < 0 ? -balance : 0m,
            Balance = balance,
            Archived = archived,
        };

    private void StubWindowFetch(params HoldedLedgerLineDto[] lines) =>
        _client.ListLedgerEntriesAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), null, Arg.Any<CancellationToken>())
            .Returns(lines);

    private (LocalDate From, LocalDate To, int? Account)[] LedgerCalls() =>
        _client.ReceivedCalls()
            .Where(c => string.Equals(c.GetMethodInfo().Name, nameof(IHoldedClient.ListLedgerEntriesAsync), StringComparison.Ordinal))
            .Select(c => c.GetArguments())
            .Select(a => (From: (LocalDate)a[0]!, To: (LocalDate)a[1]!, Account: (int?)a[2]))
            .ToArray();

    [HumansFact]
    public async Task Cold_cache_sweeps_from_inception_and_caches_all_accounts()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        StubWindowFetch(
            Dto(1, 1, 40000004, FixedNow, credit: 23m),
            Dto(1, 2, 62900140, FixedNow, debit: 23m));   // NOT creditor-block — cached anyway (full mirror)

        (await _service.SyncLedgerAsync(full: false, ct)).Should().BeTrue();

        var calls = LedgerCalls();
        calls.Should().ContainSingle();
        calls[0].From.Should().Be(new LocalDate(2020, 1, 1));
        calls[0].To.Should().Be(Today);

        (await _repo.GetLedgerLinesByAccountNumAsync(62900140, ct)).Should().ContainSingle();
        (await _repo.GetLedgerLinesByAccountNumAsync(40000004, ct)).Should().ContainSingle();
    }

    [HumansFact]
    public async Task Warm_cache_sweeps_trailing_45_day_window_anchored_on_now()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        StubWindowFetch(Dto(1, 1, 40000004, FixedNow, credit: 23m));
        await _service.SyncLedgerAsync(full: false, ct);   // cold pass fills the cache
        _client.ClearReceivedCalls();

        StubWindowFetch(Dto(2, 1, 40000004, FixedNow, credit: 5m));
        await _service.SyncLedgerAsync(full: false, ct);

        var calls = LedgerCalls();
        calls.Should().ContainSingle();
        calls[0].To.Should().Be(Today);
        calls[0].From.Should().Be(FixedNow.Minus(Duration.FromDays(45)).InZone(MadridZone).Date);
    }

    [HumansFact]
    public async Task Full_flag_forces_inception_sweep_and_replaces_deleted_lines()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        StubWindowFetch(
            Dto(1, 1, 40000004, FixedNow, credit: 23m),
            Dto(2, 1, 40000004, FixedNow, debit: 23m));    // the phantom row
        await _service.SyncLedgerAsync(full: false, ct);

        // Holded no longer returns the phantom — the full sweep must delete it, not append.
        StubWindowFetch(Dto(1, 1, 40000004, FixedNow, credit: 23m));
        await _service.SyncLedgerAsync(full: true, ct);

        var lines = await _repo.GetLedgerLinesByAccountNumAsync(40000004, ct);
        lines.Should().ContainSingle();
        lines[0].EntryNumber.Should().Be(1);
    }

    [HumansFact]
    public async Task Reconcile_repulls_mismatched_account_and_reports_residual()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        StubWindowFetch(Dto(1, 1, 40000004, FixedNow, credit: 23m));   // local balance −23
        // Chart says −23 for 40000004 (matches) but −100 for 57200001 (no local lines → drift).
        _client.ListAccountingAccountsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<HoldedAccountDto> { Account(40000004, -23m), Account(57200001, -100m) });
        // Targeted re-pull for the drifted account returns lines still summing to −50, not −100:
        // residual mismatch, reported not thrown.
        _client.ListLedgerEntriesAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), 57200001, Arg.Any<CancellationToken>())
            .Returns(new List<HoldedLedgerLineDto> { Dto(9, 1, 57200001, FixedNow, credit: 50m) });

        await _service.SyncLedgerAsync(full: false, ct);

        var calls = LedgerCalls();
        calls.Should().Contain(c => c.Account == 57200001);
        calls.Should().NotContain(c => c.Account == 40000004);   // matching account: no re-pull

        var states = await _repo.GetSyncStatesAsync(ct);
        var ledgerState = states.Single(s => s.Kind == HoldedSyncKind.FullSync); // cold cache ⇒ full
        ledgerState.LastError.Should().Contain("57200001").And.Contain("-100").And.Contain("-50");
        ledgerState.SyncStatus.Should().Be(HoldedSyncStatus.Idle);               // reported, not failed

        // The re-pulled lines landed in the mirror despite the residual.
        (await _repo.GetLedgerLinesByAccountNumAsync(57200001, ct)).Should().ContainSingle();
    }

    [HumansFact]
    public async Task Archived_accounts_are_not_reconciled()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        StubWindowFetch();
        _client.ListAccountingAccountsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<HoldedAccountDto> { Account(10000000, -999m, archived: true) });

        await _service.SyncLedgerAsync(full: false, ct);

        LedgerCalls().Should().NotContain(c => c.Account == 10000000);
    }

    [HumansFact]
    public async Task Second_concurrent_sweep_skips()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var firstStarted = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        _client.ListLedgerEntriesAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), null, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                firstStarted.TrySetResult();
                releaseFirst.Task.GetAwaiter().GetResult();
                return new List<HoldedLedgerLineDto>();
            });

        var inFlight = Task.Run(() => _service.SyncLedgerAsync(full: false, CancellationToken.None), CancellationToken.None);
        await firstStarted.Task;

        (await _service.SyncLedgerAsync(full: true, ct)).Should().BeFalse();

        releaseFirst.SetResult();
        (await inFlight).Should().BeTrue();
    }

    [HumansFact]
    public async Task Drains_call_log_into_repo()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        StubWindowFetch();
        _callLog.DrainAll().Returns(new List<HoldedApiCallRecord>
        {
            new(FixedNow, "ListLedgerEntriesAsync", "GET", 200, 42, "minute"),
        });

        await _service.SyncLedgerAsync(full: false, ct);

        var calls = await _repo.GetApiCallsAsync(ct);
        var call = calls.Should().ContainSingle().Subject;
        call.Endpoint.Should().Be("ListLedgerEntriesAsync");
        call.RateLimitRemaining.Should().Be(42);
        call.RateLimitWindow.Should().Be("minute");
    }

    [HumansFact]
    public async Task Failed_sweep_records_error_state_and_rethrows()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        _client.ListLedgerEntriesAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), null, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<HoldedLedgerLineDto>>(_ => throw new HoldedTransientException("down"));

        var act = () => _service.SyncLedgerAsync(full: false, ct);
        await act.Should().ThrowAsync<HoldedTransientException>();

        var states = await _repo.GetSyncStatesAsync(ct);
        states.Single().SyncStatus.Should().Be(HoldedSyncStatus.Error);
        states.Single().LastError.Should().Contain("down");

        // And the gate was released — the next sweep is not locked out forever.
        StubWindowFetch();
        (await _service.SyncLedgerAsync(full: false, ct)).Should().BeTrue();
    }

    [HumansFact]
    public async Task Balances_read_supports_year_filter()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        StubWindowFetch(
            Dto(1, 1, 62900105, Instant.FromUtc(2026, 3, 1, 0, 0), debit: 100m),
            Dto(2, 1, 62900105, Instant.FromUtc(2025, 3, 1, 0, 0), debit: 40m));
        await _service.SyncLedgerAsync(full: false, ct);

        (await _service.GetAccountBalancesAsync(2026, ct))[62900105].Should().Be(100m);
        (await _service.GetAccountBalancesAsync(null, ct))[62900105].Should().Be(140m);
    }
}
