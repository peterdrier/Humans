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

/// <summary>The <see cref="IHoldedService"/> reads other sections build on, and the /Holded/Accounts read model.
/// All of them answer from the mirror.</summary>
public sealed class HoldedPublicReadsTests
{
    private static readonly Instant FixedNow = Instant.FromUtc(2026, 10, 30, 12, 0);
    private static readonly DateTimeZone Madrid = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    private readonly Repository _repo;
    private readonly IHoldedClient _client = Substitute.For<IHoldedClient>();
    private readonly Service _service;

    public HoldedPublicReadsTests()
    {
        var options = new DbContextOptionsBuilder<HoldedDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _repo = new Repository(new TestDbContextFactory<HoldedDbContext>(options));
        var callLog = Substitute.For<IHoldedCallLog>();
        callLog.DrainAll().Returns([]);
        _service = new Service(_repo, _client, callLog, new FakeClock(FixedNow),
            Options.Create(new HoldedSectionOptions()), NullLogger<Service>.Instance);
    }

    private static Instant Day(int month, int day) =>
        new LocalDate(2026, month, day).AtStartOfDayInZone(Madrid).ToInstant();

    private static HoldedLedgerLine Line(int entry, int line, int account, string? type,
        decimal debit = 0m, decimal credit = 0m, Instant? date = null, string? description = null,
        string? entryId = null, bool? isChecked = null) => new()
        {
            Id = Guid.NewGuid(),
            EntryNumber = entry,
            Line = line,
            AccountNum = account,
            Date = date ?? Day(3, 1),
            Type = type,
            Description = description,
            Debit = debit,
            Credit = credit,
            EntryId = entryId,
            Checked = isChecked,
            CreatedAt = FixedNow,
            LastSyncedAt = FixedNow,
        };

    private static HoldedAccount Account(int number, string name, decimal balance = 0m, bool archived = false,
        bool posted = false) => new()
        {
            Number = number,
            HoldedId = $"id-{number}",
            Name = name,
            Group = "Holded group",
            Debit = posted || balance > 0 ? Math.Max(balance, 1m) : 0m,
            Credit = posted || balance < 0 ? Math.Max(-balance, posted && balance >= 0 ? Math.Max(balance, 1m) : 0m) : 0m,
            Balance = balance,
            Archived = archived,
            SyncedAt = FixedNow,
        };

    private Task SeedAsync(CancellationToken ct, params HoldedLedgerLine[] lines) =>
        _repo.ReplaceLedgerWindowAsync(Instant.FromUtc(2020, 1, 1, 0, 0), FixedNow, null, lines, FixedNow, ct);

    [HumansTheory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void IsConfigured_is_the_connectors(bool configured)
    {
        _client.IsConfigured.Returns(configured);
        _service.IsConfigured.Should().Be(configured);
    }

    [HumansFact]
    public async Task GetEntry_returns_every_leg_with_the_entry_id_and_tick()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedAsync(ct,
            Line(7, 2, 62900300, "purchase", debit: 121m, entryId: "e7", isChecked: true),
            Line(7, 1, 40000099, "purchase", credit: 121m, entryId: "e7"),
            Line(8, 1, 57200001, "entry", debit: 5m));

        var entry = await _service.GetEntryAsync(7, ct);

        entry.Select(l => l.Line).Should().Equal(1, 2);
        entry.Should().OnlyContain(l => l.EntryId == "e7");
        entry[1].Checked.Should().BeTrue();
        entry[0].Checked.Should().BeNull("a line not re-synced since the column arrived has no tick to report");
        (await _service.GetEntryAsync(99, ct)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetPurchaseLines_keeps_purchase_lines_on_the_account_matching_the_prefix()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedAsync(ct,
            Line(1, 1, 40000004, "purchase", credit: 50m, description: "Purchase  Expense report 12"),
            Line(2, 1, 40000004, "purchase", credit: 50m, description: "Purchase  Expense report 12"),
            Line(3, 1, 40000004, "purchase", credit: 9m, description: "Purchase  Truck"),
            Line(4, 1, 40000004, "payment", debit: 50m, description: "Purchase  Expense report 12"),
            Line(5, 1, 40000005, "purchase", credit: 50m, description: "Purchase  Expense report 12"));

        (await _service.GetPurchaseLinesAsync(40000004, ct: ct)).Should().HaveCount(3);
        var matched = await _service.GetPurchaseLinesAsync(40000004, "Purchase  Expense report", ct);
        matched.Select(l => l.EntryNumber).Should().Equal(1, 2);
    }

    [HumansFact]
    public async Task GetOpenDocumentAmounts_is_purchases_less_refunds_and_payments_per_account_in_range()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedAsync(ct,
            Line(1, 1, 40000004, "purchase", credit: 121m),
            Line(2, 1, 40000004, "payment", debit: 90m),
            Line(7, 1, 40000004, "purchaserefund", debit: 10m),
            Line(3, 1, 40000004, "entry", debit: 21m),          // a journal entry is not a payment
            Line(4, 1, 41000001, "purchase", credit: 10m),
            Line(5, 1, 41000002, "payment", debit: 3m),         // paid with no purchase mirrored
            Line(6, 1, 62900300, "purchase", debit: 121m));     // outside the range

        var open = await _service.GetOpenDocumentAmountsAsync(40000000, 41999999, ct);

        open.Should().BeEquivalentTo(new Dictionary<int, decimal>
        {
            [40000004] = 21m,
            [41000001] = 10m,
            [41000002] = -3m,
        });
    }

    [HumansFact]
    public async Task GetSumsByAccount_sums_the_inclusive_madrid_window_and_zero_fills()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedAsync(ct,
            Line(1, 1, 62900300, "purchase", debit: 100m, date: Day(3, 1)),
            Line(2, 1, 62900300, "purchase", debit: 50m, date: Day(3, 31)),
            Line(3, 1, 62900300, "purchase", debit: 7m, date: Day(4, 1)),
            Line(4, 1, 62900300, "purchaserefund", credit: 20m, date: Day(3, 15)));

        var sums = await _service.GetSumsByAccountAsync(
            new HashSet<int> { 62900300, 62900301 }, new LocalDate(2026, 3, 1), new LocalDate(2026, 3, 31), ct);

        sums.Should().BeEquivalentTo(new Dictionary<int, decimal> { [62900300] = 130m, [62900301] = 0m });
    }

    [HumansFact]
    public async Task GetChart_carries_holded_and_english_names_and_no_holded_id()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await _repo.UpsertAccountsAsync(
        [
            Account(57200001, "Banco Sabadell", 1_000m),
            Account(57000000, "Caja, euros", 20m),
            Account(62900001, "Otros servicios", archived: true),
        ], FixedNow, ct);

        var chart = await _service.GetChartAsync(ct);

        chart.Select(a => a.Number).Should().BeEquivalentTo([57000000, 57200001, 62900001]);
        var byNumber = chart.ToDictionary(a => a.Number);
        byNumber[57000000].EnglishName.Should().Be("Cash, euros");
        byNumber[57200001].EnglishName.Should().BeNull("a bank's own name is not the standard one");
        byNumber[62900001].EnglishName.Should().BeNull("629 category accounts keep their name");
        byNumber[62900001].Archived.Should().BeTrue();
        byNumber[57200001].HoldedBalance.Should().Be(1_000m);
        typeof(HoldedChartAccountInfo).GetProperties().Select(p => p.Name).Should().NotContain("HoldedId");
    }

    [HumansFact]
    public async Task Accounts_page_groups_by_leading_digit_and_hides_archived_and_empty_by_default()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await _repo.UpsertAccountsAsync(
        [
            Account(40000004, "Member A", -10m),
            Account(57200001, "Banco Sabadell", 500m),
            Account(57200002, "Old bank", 0m, archived: true),
            Account(57200003, "Never used"),
        ], FixedNow, ct);
        await SeedAsync(ct,
            Line(1, 1, 40000004, "purchase", credit: 10m),
            Line(2, 1, 57200001, "entry", debit: 400m));

        var page = await _service.GetAccountsPageAsync(new HoldedAccountsFilter(), ct);

        page.Groups.Select(g => (g.Digit, g.Name)).Should().Equal(
            (4, "Receivables and payables"), (5, "Financial accounts"));
        var bank = page.Groups[1];
        bank.Accounts.Select(a => a.Number).Should().Equal(57200001);
        bank.HoldedBalance.Should().Be(500m);
        bank.MirrorBalance.Should().Be(400m);
        bank.LineCount.Should().Be(1);
        bank.Mismatches.Should().Be(1);
        page.HiddenArchived.Should().Be(1);
        page.HiddenEmpty.Should().Be(1);

        var all = await _service.GetAccountsPageAsync(new HoldedAccountsFilter(ShowArchived: true, ShowEmpty: true), ct);
        all.Groups[1].Accounts.Select(a => a.Number).Should().Equal(57200001, 57200002, 57200003);

        var mismatched = await _service.GetAccountsPageAsync(new HoldedAccountsFilter(Mismatched: true), ct);
        mismatched.Groups.SelectMany(g => g.Accounts).Select(a => a.Number).Should().Equal(57200001);
    }

    [HumansFact]
    public async Task Statement_lines_carry_entry_id_and_tick_and_counterparties_from_their_own_entries()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedAsync(ct,
            Line(1, 1, 57200001, "entry", debit: 50m, entryId: "e1", isChecked: true),
            Line(1, 2, 40000010, "entry", credit: 50m, entryId: "e1"),
            Line(2, 1, 62900000, "purchase", debit: 9m));

        var statement = await _service.GetAccountStatementAsync(57200001, ct);

        var line = statement!.Lines.Should().ContainSingle().Subject;
        line.EntryId.Should().Be("e1");
        line.Checked.Should().BeTrue();
        line.Counterparty!.AccountNum.Should().Be(40000010);

        var entry = await _service.GetEntryViewAsync(1, ct);
        entry!.EntryId.Should().Be("e1");
    }

    [HumansFact]
    public async Task Summary_counts_the_mirror_and_estimates_a_full_resync()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        _client.IsConfigured.Returns(false);
        await _repo.UpsertAccountsAsync([Account(57200001, "Banco", 50m)], FixedNow, ct);
        await SeedAsync(ct,
            Line(1, 1, 57200001, "entry", debit: 50m),
            Line(1, 2, 40000010, "entry", credit: 50m));

        var summary = await _service.GetSummaryAsync(ct);

        summary.IsConfigured.Should().BeFalse();
        summary.ApiReachable.Should().BeFalse();
        summary.LedgerLineCount.Should().Be(2);
        summary.EntryCount.Should().Be(1);
        summary.AccountCount.Should().Be(2, "an account only seen on a line still counts");
        summary.MismatchCount.Should().Be(1, "40000010 has a line but no chart balance");
        summary.FullSyncCalls.Should().Be(2);
        await _client.DidNotReceive().GetUsageAsync(Arg.Any<CancellationToken>());
    }
}
