using AwesomeAssertions;
using Humans.Holded.Contracts;
using Humans.Holded.Data;
using Humans.Holded.Domain;
using Humans.Holded.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

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

    public enum SyncPath { Ledger, PurchaseDocs, Contacts, BankMovements, Accounts }
    public enum SyncBoundary { Startup, BeforeReplacement, Completion, CompletionFailure }

    [HumansTheory]
    [InlineData(SyncPath.Ledger, SyncBoundary.Startup)]
    [InlineData(SyncPath.Ledger, SyncBoundary.BeforeReplacement)]
    [InlineData(SyncPath.Ledger, SyncBoundary.Completion)]
    [InlineData(SyncPath.Ledger, SyncBoundary.CompletionFailure)]
    [InlineData(SyncPath.PurchaseDocs, SyncBoundary.Startup)]
    [InlineData(SyncPath.PurchaseDocs, SyncBoundary.BeforeReplacement)]
    [InlineData(SyncPath.PurchaseDocs, SyncBoundary.Completion)]
    [InlineData(SyncPath.PurchaseDocs, SyncBoundary.CompletionFailure)]
    [InlineData(SyncPath.Contacts, SyncBoundary.Startup)]
    [InlineData(SyncPath.Contacts, SyncBoundary.BeforeReplacement)]
    [InlineData(SyncPath.Contacts, SyncBoundary.Completion)]
    [InlineData(SyncPath.Contacts, SyncBoundary.CompletionFailure)]
    [InlineData(SyncPath.BankMovements, SyncBoundary.BeforeReplacement)]
    [InlineData(SyncPath.BankMovements, SyncBoundary.Completion)]
    [InlineData(SyncPath.BankMovements, SyncBoundary.CompletionFailure)]
    [InlineData(SyncPath.Accounts, SyncBoundary.BeforeReplacement)]
    [InlineData(SyncPath.Accounts, SyncBoundary.Completion)]
    [InlineData(SyncPath.Accounts, SyncBoundary.CompletionFailure)]
    public async Task Mirror_outcomes_respect_startup_and_completion_boundaries(SyncPath path, SyncBoundary boundary)
    {
        using var shutdown = new CancellationTokenSource();
        var repository = Substitute.For<IHoldedMirrorRepository>();
        repository.HasAnyLedgerLinesAsync(Arg.Any<CancellationToken>()).Returns(true);
        var states = Enum.GetValues<HoldedSyncKind>().ToDictionary(k => k, k => new HoldedSyncState { Kind = k });
        var kind = Enum.Parse<HoldedSyncKind>(path.ToString());
        var state = states[kind];
        repository.GetOrCreateSyncStateAsync(Arg.Any<HoldedSyncKind>(), Arg.Any<CancellationToken>())
            .Returns(call => states[call.Arg<HoldedSyncKind>()]);
        repository.GetAccountTotalsAsync(Arg.Any<CancellationToken>()).Returns([]);
        var startupFailure = new IOException("Cannot start sync");
        var completionFailure = new OperationCanceledException("Completion dependency canceled");
        CancellationToken? completionToken = null;
        repository.SaveSyncStateAsync(Arg.Any<HoldedSyncState>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var saved = call.Arg<HoldedSyncState>();
            if (saved.Kind != kind) return;
            if (saved.SyncStatus == HoldedSyncStatus.Running && boundary == SyncBoundary.Startup) throw startupFailure;
            if (saved.SyncStatus != HoldedSyncStatus.Idle) return;
            completionToken = call.Arg<CancellationToken>();
            await shutdown.CancelAsync();
            if (boundary == SyncBoundary.CompletionFailure) throw completionFailure;
            completionToken.Value.ThrowIfCancellationRequested();
        });
        async Task PrepareAsync(SyncPath selected)
        {
            if (boundary == SyncBoundary.BeforeReplacement && path == selected) await shutdown.CancelAsync();
        }
        _client.ListLedgerEntriesAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), null, Arg.Any<CancellationToken>())
            .Returns(async Task<IReadOnlyList<HoldedLedgerLineDto>> (_) => { await PrepareAsync(SyncPath.Ledger); return []; });
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>())
            .Returns(async Task<IReadOnlyList<HoldedPurchaseDocListItemDto>> (_) => { await PrepareAsync(SyncPath.PurchaseDocs); return []; });
        _client.ListContactsAsync(Arg.Any<CancellationToken>())
            .Returns(async Task<IReadOnlyList<HoldedContactDto>> (_) => { await PrepareAsync(SyncPath.Contacts); return []; });
        _client.ListBankMovementsAsync("treasury", Today, Today, Arg.Any<CancellationToken>(), null)
            .Returns(async Task<IReadOnlyList<HoldedBankMovementDto>> (_) => { await PrepareAsync(SyncPath.BankMovements); return []; });
        _client.ListAccountingAccountsAsync(Arg.Any<CancellationToken>())
            .Returns(async Task<IReadOnlyList<HoldedAccountDto>> (_) => { await PrepareAsync(SyncPath.Accounts); return []; });
        var service = new Service(repository, _client, _callLog, new FakeClock(FixedNow),
            Options.Create(new HoldedSectionOptions()), NullLogger<Service>.Instance);
        Task RunAsync() => path switch
        {
            SyncPath.Ledger => service.SyncLedgerAsync(ct: shutdown.Token),
            SyncPath.PurchaseDocs => service.SyncPurchaseDocumentsAsync(shutdown.Token),
            SyncPath.Contacts => service.SyncContactsAsync(shutdown.Token),
            SyncPath.BankMovements => service.SyncBankMovementsAsync("treasury", Today, Today, ct: shutdown.Token),
            _ => service.SyncChartAsync(shutdown.Token)
        };

        var error = await Record.ExceptionAsync(RunAsync);
        if (boundary == SyncBoundary.Startup)
        {
            error.Should().BeSameAs(startupFailure);
            state.SyncStatus.Should().Be(HoldedSyncStatus.Error);
            await repository.Received(1).SaveSyncStateAsync(state, CancellationToken.None);
        }
        else if (boundary == SyncBoundary.BeforeReplacement)
        {
            error.Should().BeOfType<OperationCanceledException>();
            await repository.DidNotReceiveWithAnyArgs().ReplaceLedgerWindowAsync(default, default, default, default!, default);
            await repository.DidNotReceiveWithAnyArgs().ReplacePurchaseDocsAsync(default!, default);
            await repository.DidNotReceiveWithAnyArgs().ReplaceContactsAsync(default!, default);
            await repository.DidNotReceiveWithAnyArgs().ReplaceBankMovementsAsync(default!, default, default, default!, default);
            await repository.DidNotReceiveWithAnyArgs().UpsertAccountsAsync(default!, default);
        }
        else
        {
            completionToken.Should().Be(CancellationToken.None);
            if (boundary == SyncBoundary.CompletionFailure)
                error.Should().BeOfType<InvalidOperationException>().Which.InnerException.Should().BeSameAs(completionFailure);
            else
            {
                error.Should().BeNull();
                state.SyncStatus.Should().Be(HoldedSyncStatus.Idle);
                state.LastSyncAt.Should().Be(FixedNow);
                _callLog.Received().DrainAll();
            }
        }
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Committed_chart_or_targeted_window_finishes_its_call_log(bool chart)
    {
        using var shutdown = new CancellationTokenSource();
        var repository = Substitute.For<IHoldedMirrorRepository>();
        repository.GetOrCreateSyncStateAsync(HoldedSyncKind.Accounts, Arg.Any<CancellationToken>())
            .Returns(new HoldedSyncState { Kind = HoldedSyncKind.Accounts });
        repository.UpsertAccountsAsync(Arg.Any<IReadOnlyList<HoldedAccount>>(), FixedNow, shutdown.Token)
            .Returns(async _ => await shutdown.CancelAsync());
        repository.ReplaceLedgerWindowAsync(Arg.Any<Instant>(), FixedNow, 123,
                Arg.Any<IReadOnlyList<HoldedLedgerLine>>(), FixedNow, shutdown.Token)
            .Returns(async _ => await shutdown.CancelAsync());
        StubWindowFetch();
        var service = new Service(repository, _client, _callLog, new FakeClock(FixedNow),
            Options.Create(new HoldedSectionOptions()), NullLogger<Service>.Instance);
        Func<Task> action = chart ? () => service.SyncChartAsync(shutdown.Token)
            : () => service.SyncAccountLedgerAsync(123, shutdown.Token);

        await action.Should().NotThrowAsync();
        _callLog.Received(1).DrainAll();
        if (chart)
            await repository.Received(1).GetOrCreateSyncStateAsync(HoldedSyncKind.Accounts, CancellationToken.None);
    }

    [HumansFact]
    public async Task Consumed_call_log_cancellation_is_a_persistence_failure()
    {
        var repository = Substitute.For<IHoldedMirrorRepository>();
        _callLog.DrainAll().Returns([new HoldedApiCallRecord(FixedNow, "ledger", "GET", 200, null, null)]);
        var canceled = new OperationCanceledException("Call log save canceled");
        repository.AddApiCallsAsync(Arg.Any<IReadOnlyList<HoldedApiCall>>(), CancellationToken.None)
            .Returns(Task.FromException(canceled));
        var sync = new HoldedMirrorSync(repository, _client, _callLog, new FakeClock(FixedNow),
            NullLogger<Service>.Instance, new HoldedAccountReader(repository));

        var action = () => sync.DrainCallLogAsync(TestContext.Current.CancellationToken);
        (await action.Should().ThrowAsync<InvalidOperationException>()).Which.InnerException.Should().BeSameAs(canceled);
    }

    [HumansFact]
    public async Task Shutdown_after_the_ledger_window_does_not_claim_reconciliation_finished()
    {
        using var shutdown = new CancellationTokenSource();
        var repository = Substitute.For<IHoldedMirrorRepository>();
        repository.HasAnyLedgerLinesAsync(shutdown.Token).Returns(true);
        var state = new HoldedSyncState { Kind = HoldedSyncKind.Ledger, LastSyncAt = FixedNow - Duration.FromDays(1) };
        repository.GetOrCreateSyncStateAsync(HoldedSyncKind.Ledger, shutdown.Token).Returns(state);
        repository.GetOrCreateSyncStateAsync(HoldedSyncKind.Accounts, Arg.Any<CancellationToken>())
            .Returns(new HoldedSyncState { Kind = HoldedSyncKind.Accounts });
        repository.GetAccountTotalsAsync(Arg.Any<CancellationToken>()).Returns([]);
        repository.ReplaceLedgerWindowAsync(Arg.Any<Instant>(), FixedNow, null,
                Arg.Any<IReadOnlyList<HoldedLedgerLine>>(), FixedNow, shutdown.Token)
            .Returns(async _ => await shutdown.CancelAsync());
        StubWindowFetch();
        var service = new Service(repository, _client, _callLog, new FakeClock(FixedNow),
            Options.Create(new HoldedSectionOptions()), NullLogger<Service>.Instance);

        var action = () => service.SyncLedgerAsync(ct: shutdown.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        state.SyncStatus.Should().Be(HoldedSyncStatus.Error);
        state.LastSyncAt.Should().Be(FixedNow - Duration.FromDays(1));
        await _client.DidNotReceive().ListAccountingAccountsAsync(Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Cold_cache_sweeps_from_inception_and_caches_all_accounts()
    {
        var ct = TestContext.Current.CancellationToken;
        StubWindowFetch(
            Dto(1, 1, 40000004, FixedNow, credit: 23m),
            Dto(1, 2, 62900140, FixedNow, debit: 23m));   // NOT creditor-block — cached anyway (full mirror)
        (await _service.GetLedgerSyncedAtAsync(ct)).Should().BeNull();

        (await _service.SyncLedgerAsync(full: false, ct)).Should().BeTrue();
        (await _service.GetLedgerSyncedAtAsync(ct)).Should().Be(FixedNow);

        var calls = LedgerCalls();
        calls.Should().ContainSingle();
        calls[0].From.Should().Be(new LocalDate(2020, 1, 1));
        calls[0].To.Should().Be(Today);

        (await _repo.GetLedgerLinesByAccountNumAsync(62900140, ct: ct)).Should().ContainSingle();
        (await _repo.GetLedgerLinesByAccountNumAsync(40000004, ct: ct)).Should().ContainSingle();
    }

    [HumansFact]
    public async Task Warm_cache_sweeps_trailing_45_day_window_anchored_on_now()
    {
        var ct = TestContext.Current.CancellationToken;
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
        var ct = TestContext.Current.CancellationToken;
        StubWindowFetch(
            Dto(1, 1, 40000004, FixedNow, credit: 23m),
            Dto(2, 1, 40000004, FixedNow, debit: 23m));    // the phantom row
        await _service.SyncLedgerAsync(full: false, ct);

        // Holded no longer returns the phantom — the full sweep must delete it, not append.
        StubWindowFetch(Dto(1, 1, 40000004, FixedNow, credit: 23m));
        await _service.SyncLedgerAsync(full: true, ct);

        var lines = await _repo.GetLedgerLinesByAccountNumAsync(40000004, ct: ct);
        lines.Should().ContainSingle();
        lines[0].EntryNumber.Should().Be(1);
    }

    [HumansFact]
    public async Task Reconcile_repulls_mismatched_account_and_reports_residual()
    {
        var ct = TestContext.Current.CancellationToken;
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
        (await _repo.GetLedgerLinesByAccountNumAsync(57200001, ct: ct)).Should().ContainSingle();
    }

    [HumansFact]
    public async Task Archived_accounts_are_not_reconciled()
    {
        var ct = TestContext.Current.CancellationToken;
        StubWindowFetch();
        _client.ListAccountingAccountsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<HoldedAccountDto> { Account(10000000, -999m, archived: true) });

        await _service.SyncLedgerAsync(full: false, ct);

        LedgerCalls().Should().NotContain(c => c.Account == 10000000);
    }

    [HumansFact]
    public async Task Second_concurrent_sweep_skips()
    {
        var ct = TestContext.Current.CancellationToken;
        var firstStarted = new TaskCompletionSource();
        using var releaseFirst = new SemaphoreSlim(0);
        _client.ListLedgerEntriesAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), null, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                firstStarted.TrySetResult();
                await releaseFirst.WaitAsync(CancellationToken.None);
                return (IReadOnlyList<HoldedLedgerLineDto>)new List<HoldedLedgerLineDto>();
            });

        var inFlight = Task.Run(() => _service.SyncLedgerAsync(full: false, CancellationToken.None), CancellationToken.None);
        await firstStarted.Task;

        (await _service.SyncLedgerAsync(full: true, ct)).Should().BeFalse();

        releaseFirst.Release();
        (await inFlight).Should().BeTrue();
    }

    [HumansFact]
    public async Task Drains_call_log_into_repo()
    {
        var ct = TestContext.Current.CancellationToken;
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

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancelled_sweep_distinguishes_request_abort_from_dependency_failure(bool requestAborted)
    {
        using var cancellation = new CancellationTokenSource();
        var logger = new CapturingLogger<Service>();
        var service = new Service(_repo, _client, _callLog, new FakeClock(FixedNow),
            Options.Create(new HoldedSectionOptions()), logger);
        _client.ListLedgerEntriesAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), null, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<HoldedLedgerLineDto>>(_ =>
            {
                if (requestAborted) cancellation.Cancel();
                throw new OperationCanceledException("ledger fetch cancelled");
            });

        var act = () => service.SyncLedgerAsync(full: false, cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(requestAborted ? LogLevel.Warning : LogLevel.Error);
        if (requestAborted) entry.Exception.Should().BeNull();
        else entry.Exception.Should().BeOfType<OperationCanceledException>();
        var states = await _repo.GetSyncStatesAsync(TestContext.Current.CancellationToken);
        states.Single().SyncStatus.Should().Be(HoldedSyncStatus.Error);
        states.Single().LastError.Should().Contain("ledger fetch cancelled");
    }

    [HumansFact]
    public async Task Manual_sync_propagates_request_abort_with_a_stack_free_warning()
    {
        using var cancellation = new CancellationTokenSource();
        var holded = Substitute.For<IHoldedService>();
        holded.SyncLedgerAsync(false, cancellation.Token).Returns<bool>(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        var logger = new CapturingLogger<Humans.Holded.Controllers.HoldedController>();
        var controller = new Humans.Holded.Controllers.HoldedController(
            Substitute.For<Humans.Users.Contracts.IUserService>(),
            Substitute.For<IHoldedAdminService>(), holded, logger)
        {
            TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
                new Microsoft.AspNetCore.Http.DefaultHttpContext(),
                Substitute.For<Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider>()),
        };

        var act = () => controller.SyncNow(cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Exception.Should().BeNull();
    }

    [HumansTheory]
    [InlineData(0, "down", "down")]
    [InlineData(1997, "tail", "…")]
    [InlineData(1996, "😀tail", "…")]
    [InlineData(1995, "😀tail", "😀…")]
    public async Task Failed_sweep_records_error_state_and_rethrows(int prefixLength, string tail, string expectedTail)
    {
        var prefix = new string('x', prefixLength);
        var message = prefix + tail;
        var ct = TestContext.Current.CancellationToken;
        _client.ListLedgerEntriesAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), null, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<HoldedLedgerLineDto>>(_ => throw new HoldedTransientException(message));

        var act = () => _service.SyncLedgerAsync(full: false, ct);
        var failure = await act.Should().ThrowAsync<HoldedTransientException>();
        failure.Which.Message.Should().Be(message);

        var states = await _repo.GetSyncStatesAsync(ct);
        states.Single().SyncStatus.Should().Be(HoldedSyncStatus.Error);
        states.Single().LastError.Should().Be(prefix + expectedTail);

        // And the gate was released — the next sweep is not locked out forever.
        StubWindowFetch();
        (await _service.SyncLedgerAsync(full: false, ct)).Should().BeTrue();
    }

    [HumansFact]
    public async Task Balances_read_sums_the_whole_mirror()
    {
        var ct = TestContext.Current.CancellationToken;
        StubWindowFetch(
            Dto(1, 1, 62900105, Instant.FromUtc(2026, 3, 1, 0, 0), debit: 100m),
            Dto(2, 1, 62900105, Instant.FromUtc(2025, 3, 1, 0, 0), debit: 40m));
        await _service.SyncLedgerAsync(full: false, ct);

        // Every cached line, whatever its date — reconciliation compares this against Holded's
        // own all-time chart balance, so any date narrowing here would manufacture drift.
        (await _service.GetAccountBalancesAsync(ct))[62900105].Should().Be(140m);
    }

    [HumansFact]
    public async Task Account_repull_replaces_only_that_accounts_whole_history()
    {
        var ct = TestContext.Current.CancellationToken;
        var june = Instant.FromUtc(2026, 6, 12, 0, 0);
        StubWindowFetch(
            Dto(1, 1, 57200003, june, credit: 10m),
            Dto(2, 1, 57200003, june, credit: 99m),
            Dto(3, 1, 57200001, june, credit: 5m));
        await _service.SyncLedgerAsync(full: true, ct);

        _client.ListLedgerEntriesAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), 57200003, Arg.Any<CancellationToken>())
            .Returns([Dto(1, 1, 57200003, june, credit: 10m), Dto(4, 1, 57200003, june, credit: 7m)]);
        (await _service.SyncAccountLedgerAsync(57200003, ct)).Should().BeTrue();

        LedgerCalls().Last().Should().Be((new LocalDate(2020, 1, 1), Today, (int?)57200003));
        (await _repo.GetLedgerLinesByAccountNumAsync(57200003, ct: ct)).Select(l => l.EntryNumber)
            .Should().BeEquivalentTo([1, 4]);
        (await _repo.GetLedgerLinesByAccountNumAsync(57200001, ct: ct)).Should().ContainSingle();
    }
}
