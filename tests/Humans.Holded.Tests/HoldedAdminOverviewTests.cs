using AwesomeAssertions;
using Humans.Holded.Contracts;
using Humans.Holded.Data;
using Humans.Holded.Domain;
using Humans.Holded.Models;
using Humans.Holded.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Holded.Tests;

/// <summary>
/// The /Holded screen's read model. Every number on the page comes from the mirror — the one
/// live call is GET /usage, and its failure must not blank the rest of the page.
/// </summary>
public sealed class HoldedAdminOverviewTests
{
    private static readonly Instant FixedNow = Instant.FromUtc(2026, 8, 10, 12, 0);

    private readonly Repository _repo;
    private readonly IHoldedClient _client = Substitute.For<IHoldedClient>();
    private readonly IHoldedCallLog _callLog = Substitute.For<IHoldedCallLog>();
    private readonly Service _service;

    public HoldedAdminOverviewTests()
    {
        var options = new DbContextOptionsBuilder<HoldedDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _repo = new Repository(new TestDbContextFactory<HoldedDbContext>(options));
        _callLog.DrainAll().Returns([]);
        _client.GetUsageAsync(Arg.Any<CancellationToken>())
            .Returns(new HoldedUsageDto { Period = "2026-08", Usage = 36, Limit = 2_000_000 });
        _service = new Service(_repo, _client, _callLog, new FakeClock(FixedNow),
            Options.Create(new HoldedSectionOptions()), NullLogger<Service>.Instance);
    }

    private static HoldedApiCall Call(Instant at, string endpoint) => new()
    {
        Id = Guid.NewGuid(),
        CalledAt = at,
        Endpoint = endpoint,
        Method = "GET",
        StatusCode = 200,
    };

    private static HoldedAccount Account(
        int number, decimal balance, bool archived = false,
        decimal? debit = null, decimal? credit = null) => new()
        {
            Number = number,
            HoldedId = $"id-{number}",
            Name = $"acct {number}",
            Group = "Gastos",
            // Overridable together for the accounts whose debits and credits cancel: balance alone
            // cannot tell "never posted to" from "posted to all year and back to zero".
            Debit = debit ?? (balance > 0 ? balance : 0m),
            Credit = credit ?? (balance < 0 ? -balance : 0m),
            Balance = balance,
            Archived = archived,
            SyncedAt = FixedNow,
        };

    private static HoldedLedgerLine Line(int entry, int account, decimal debit = 0m, decimal credit = 0m,
        int line = 1, Instant? date = null) => new()
        {
            Id = Guid.NewGuid(),
            EntryNumber = entry,
            Line = line,
            AccountNum = account,
            Date = date ?? FixedNow,
            Debit = debit,
            Credit = credit,
            CreatedAt = FixedNow,
            LastSyncedAt = FixedNow,
        };

    private Task SeedLinesAsync(CancellationToken ct, params HoldedLedgerLine[] lines) =>
        _repo.ReplaceLedgerWindowAsync(
            Instant.FromUtc(2020, 1, 1, 0, 0), FixedNow, null, lines, FixedNow, ct);

    [HumansFact]
    public async Task Calls_group_by_madrid_month_newest_first_with_per_endpoint_counts()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await _repo.AddApiCallsAsync(
        [
            Call(Instant.FromUtc(2026, 7, 4, 10, 0), "ListLedgerEntriesAsync"),
            // 01:30 on 1 August in Madrid — the month bucket is the local one, not UTC's.
            Call(Instant.FromUtc(2026, 7, 31, 23, 30), "ListLedgerEntriesAsync"),
            Call(Instant.FromUtc(2026, 8, 3, 10, 0), "GetUsageAsync"),
            Call(Instant.FromUtc(2026, 8, 4, 10, 0), "ListLedgerEntriesAsync"),
        ], ct);

        var overview = await _service.GetOverviewAsync(ct);

        overview.CallsByMonth.Should().HaveCount(2);
        overview.CallsByMonth[0].Year.Should().Be(2026);
        overview.CallsByMonth[0].Month.Should().Be(8);
        overview.CallsByMonth[0].Total.Should().Be(3);
        overview.CallsByMonth[0].ByEndpoint["ListLedgerEntriesAsync"].Should().Be(2);
        overview.CallsByMonth[0].ByEndpoint["GetUsageAsync"].Should().Be(1);
        overview.CallsByMonth[1].Month.Should().Be(7);
        overview.CallsByMonth[1].Total.Should().Be(1);

        overview.MonthlyCallBudget.Should().Be(2000);
        overview.Usage!.Period.Should().Be("2026-08");
        overview.ApiReachable.Should().BeTrue();
    }

    [HumansFact]
    public async Task Archived_accounts_are_dropped_and_the_chart_is_ordered_by_number()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await _repo.UpsertAccountsAsync(
        [
            Account(62900128, 121_684.00m),
            Account(62900000, 133_416.45m),
            Account(40000004, -53_203.00m),
            Account(62999999, 999m, archived: true),
        ], FixedNow, ct);

        var overview = await _service.GetOverviewAsync(ct);

        overview.Accounts.Select(a => a.Number).Should().Equal(40000004, 62900000, 62900128);
    }

    [HumansFact]
    public async Task Holded_postings_are_reported_even_when_debits_and_credits_cancel()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await _repo.UpsertAccountsAsync(
        [
            Account(55500001, 0m, debit: 5_000m, credit: 5_000m),   // posted to, back to zero
            Account(62900140, 0m),                                  // never posted to
            Account(62900141, 10m),                                 // posted to, drifted
        ], FixedNow, ct);

        var overview = await _service.GetOverviewAsync(ct);
        var byNumber = overview.Accounts.ToDictionary(a => a.Number);

        // The cancelling account reconciles against an empty mirror, so Reconciled cannot carry
        // this and HoldedBalance cannot either — both read exactly like the untouched account.
        byNumber[55500001].Reconciled.Should().BeTrue();
        byNumber[55500001].HoldedBalance.Should().Be(0m);
        byNumber[55500001].LocalLineCount.Should().Be(0);
        byNumber[55500001].HoldedHasPostings.Should().BeTrue();

        byNumber[62900140].HoldedHasPostings.Should().BeFalse();
        byNumber[62900141].HoldedHasPostings.Should().BeTrue();
    }

    [HumansFact]
    public async Task Accounts_carry_the_english_pgc_group_not_holdeds_spanish_one()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await _repo.UpsertAccountsAsync(
        [
            Account(40000004, -53_203.00m),
            Account(57200001, 418_840.54m),
            Account(62900000, 133_416.45m),
            Account(75900001, -5_000.00m),
        ], FixedNow, ct);

        var overview = await _service.GetOverviewAsync(ct);

        // Number order is group order, which is what lets the view group without sorting again.
        overview.Accounts.Select(a => a.Group).Should().Equal(
            "Receivables and payables",
            "Financial accounts",
            "Purchases and expenses",
            "Sales and income");
    }

    [HumansFact]
    public async Task Reconciled_compares_the_holded_balance_against_the_local_ledger_sum()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await _repo.UpsertAccountsAsync(
        [
            Account(40000004, -53_203.00m),      // matches the cached lines
            Account(57200001, 418_840.54m),      // the standing Sabadell mismatch
            Account(62900140, 0m),               // no lines, no balance — reconciled by definition
            Account(62900141, 10m),              // no lines but a balance — drifted
        ], FixedNow, ct);
        await SeedLinesAsync(ct,
            Line(1, 40000004, credit: 53_203.00m),
            Line(2, 57200001, debit: 771_074.85m));

        var overview = await _service.GetOverviewAsync(ct);
        var byNumber = overview.Accounts.ToDictionary(a => a.Number);

        byNumber[40000004].LocalBalance.Should().Be(-53_203.00m);
        byNumber[40000004].LocalLineCount.Should().Be(1);
        byNumber[40000004].Reconciled.Should().BeTrue();

        byNumber[57200001].LocalBalance.Should().Be(771_074.85m);
        byNumber[57200001].Reconciled.Should().BeFalse();

        byNumber[62900140].LocalBalance.Should().BeNull();
        byNumber[62900140].LocalLineCount.Should().Be(0);
        byNumber[62900140].Reconciled.Should().BeTrue();

        byNumber[62900141].LocalBalance.Should().BeNull();
        byNumber[62900141].Reconciled.Should().BeFalse();

        overview.LedgerLineCount.Should().Be(2);
    }

    [HumansFact]
    public async Task Usage_failure_leaves_usage_null_and_the_rest_populated()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        _client.GetUsageAsync(Arg.Any<CancellationToken>())
            .Returns<HoldedUsageDto>(_ => throw new HoldedTransientException("holded down"));
        await _repo.UpsertAccountsAsync([Account(62900000, 133_416.45m)], FixedNow, ct);
        await SeedLinesAsync(ct, Line(1, 62900000, debit: 133_416.45m));

        var overview = await _service.GetOverviewAsync(ct);

        overview.Usage.Should().BeNull();
        overview.ApiReachable.Should().BeFalse();
        overview.MonthlyCallBudget.Should().Be(2000);
        overview.Accounts.Should().ContainSingle();
        overview.LedgerLineCount.Should().Be(1);
    }

    [HumansFact]
    public async Task Overview_drains_the_call_log_before_reading_it()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        _callLog.DrainAll().Returns(new List<HoldedApiCallRecord>
        {
            new(Instant.FromUtc(2026, 8, 9, 9, 0), "ListLedgerEntriesAsync", "GET", 200, 42, "minute"),
        });

        var overview = await _service.GetOverviewAsync(ct);

        overview.CallsByMonth.Should().ContainSingle()
            .Which.ByEndpoint["ListLedgerEntriesAsync"].Should().Be(1);
    }

    [HumansFact]
    public async Task Sync_states_are_reported_per_kind()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var state = await _repo.GetOrCreateSyncStateAsync(HoldedSyncKind.Ledger, ct);
        state.LastSyncAt = FixedNow;
        state.LastCount = 17;
        state.LastError = "57200001: holded 418840.54, local 771074.85";
        await _repo.SaveSyncStateAsync(state, ct);

        var overview = await _service.GetOverviewAsync(ct);

        var row = overview.SyncStates.Should().ContainSingle().Subject;
        row.Kind.Should().Be("Ledger");
        row.Status.Should().Be("Idle");
        row.LastCount.Should().Be(17);
        row.LastError.Should().Contain("57200001");
    }

    // ─── GL account page ──────────────────────────────────────────────────────────

    [HumansFact]
    public async Task GetAccountStatement_unknown_number_returns_null()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await _repo.UpsertAccountsAsync([Account(62900128, 121_684.00m)], FixedNow, ct);

        (await _service.GetAccountStatementAsync(62900999, ct)).Should().BeNull();
    }

    [HumansFact]
    public async Task GetAccountStatement_returns_header_and_ordered_lines()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var earlier = Instant.FromUtc(2026, 8, 1, 10, 0);
        await _repo.UpsertAccountsAsync([Account(62900128, 121_684.00m), Account(40000004, -53_203.00m)], FixedNow, ct);
        await SeedLinesAsync(ct,
            Line(9, 62900128, debit: 1_684.00m, line: 2),
            Line(9, 62900128, debit: 120_000.00m, line: 1),
            Line(3, 62900128, debit: 100m, date: earlier),
            Line(4, 40000004, credit: 53_203.00m));

        var statement = await _service.GetAccountStatementAsync(62900128, ct);

        statement.Should().NotBeNull();
        statement.Account.Name.Should().Be("acct 62900128");
        // Derived from the 6 in 62900128, not the "Gastos" the chart cache carries.
        statement.Account.Group.Should().Be("Purchases and expenses");
        statement.Account.HoldedBalance.Should().Be(121_684.00m);
        statement.Account.LocalBalance.Should().Be(121_784.00m);
        statement.Account.LocalLineCount.Should().Be(3);
        statement.Account.Reconciled.Should().BeFalse();
        // Date first, then entry/line — the other account's line is not here at all.
        statement.Lines.Select(l => (l.EntryNumber, l.Line)).Should().Equal((3, 1), (9, 1), (9, 2));
    }

    [HumansFact]
    public async Task GetAccountStatement_account_absent_from_the_chart_still_renders_its_cached_lines()
    {
        // A line can name an account the chart cache has not seen yet (new account, sync ordering).
        // Showing the lines beats 404ing on a page whose whole job is the lines.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedLinesAsync(ct, Line(1, 57200001, debit: 771_074.85m));

        var statement = await _service.GetAccountStatementAsync(57200001, ct);

        statement.Should().NotBeNull();
        statement.Account.Number.Should().Be(57200001);
        statement.Account.HoldedBalance.Should().Be(0m);
        statement.Account.LocalBalance.Should().Be(771_074.85m);
        statement.Account.Reconciled.Should().BeFalse();
        statement.Lines.Should().ContainSingle();
    }

    [HumansFact]
    public async Task Monthly_call_budget_comes_from_configuration()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var service = new Service(_repo, _client, _callLog, new FakeClock(FixedNow),
            Options.Create(new HoldedSectionOptions { MonthlyCallBudget = 5000 }),
            NullLogger<Service>.Instance);

        (await service.GetOverviewAsync(ct)).MonthlyCallBudget.Should().Be(5000);
    }
}
