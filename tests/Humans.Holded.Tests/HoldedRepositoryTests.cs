using AwesomeAssertions;
using Humans.Holded.Data;
using Humans.Holded.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Holded.Tests;

public sealed class HoldedRepositoryTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 8, 10, 12, 0);
    private static readonly Instant WindowFrom = Instant.FromUtc(2026, 6, 1, 0, 0);
    private static readonly Instant WindowTo = Instant.FromUtc(2026, 6, 30, 0, 0);

    private readonly Repository _repo;

    public HoldedRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<HoldedDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _repo = new Repository(new TestDbContextFactory<HoldedDbContext>(options));
    }

    private static HoldedLedgerLine Line(
        int entryNumber, int line, int accountNum, Instant date, decimal debit = 0m, decimal credit = 0m) =>
        new()
        {
            Id = Guid.NewGuid(),
            EntryNumber = entryNumber,
            Line = line,
            AccountNum = accountNum,
            Date = date,
            Type = "payment",
            Description = "",
            Debit = debit,
            Credit = credit,
        };

    private Task SeedAsync(params HoldedLedgerLine[] lines) =>
        // Seeding through the same window replace the sync uses: a window wide enough to hold
        // every seed row, with nothing cached yet, is a pure insert.
        _repo.ReplaceLedgerWindowAsync(
            Instant.FromUtc(2000, 1, 1, 0, 0), Instant.FromUtc(2100, 1, 1, 0, 0),
            null, lines, Now, Xunit.TestContext.Current.CancellationToken);

    private async Task<IReadOnlyList<HoldedLedgerLine>> AllLinesAsync(CancellationToken ct)
    {
        var accounts = (await _repo.GetAccountTotalsAsync(ct)).Select(t => t.AccountNum);
        var lines = new List<HoldedLedgerLine>();
        foreach (var a in accounts) lines.AddRange(await _repo.GetLedgerLinesByAccountNumAsync(a, ct: ct));
        return lines;
    }

    [HumansFact]
    public async Task Account_totals_are_grouped_per_account()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedAsync(
            Line(1, 1, 40000004, Now, credit: 100m),
            Line(2, 1, 40000004, Now, debit: 30m),
            Line(2, 2, 57200001, Now, credit: 30m));

        var totals = (await _repo.GetAccountTotalsAsync(ct)).ToDictionary(t => t.AccountNum);

        totals[40000004].Should().Be(new HoldedAccountTotals(40000004, 30m, 100m, 2));
        totals[40000004].Balance.Should().Be(-70m);
        totals[57200001].LineCount.Should().Be(1);
        (await _repo.GetMirrorCountsAsync(ct)).Should().Be((3, 2));
    }

    [HumansFact]
    public async Task Lines_by_account_narrow_by_window_and_type()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var purchase = Line(1, 1, 40000004, Instant.FromUtc(2026, 3, 1, 0, 0), credit: 100m);
        purchase.Type = "purchase";
        await SeedAsync(
            purchase,
            Line(2, 1, 40000004, Instant.FromUtc(2026, 3, 2, 0, 0), debit: 100m),
            Line(3, 1, 40000004, Instant.FromUtc(2026, 5, 1, 0, 0), debit: 1m));

        (await _repo.GetLedgerLinesByAccountNumAsync(40000004, ct: ct)).Should().HaveCount(3);
        (await _repo.GetLedgerLinesByAccountNumAsync(
                40000004, Instant.FromUtc(2026, 3, 1, 0, 0), Instant.FromUtc(2026, 5, 1, 0, 0), ct: ct))
            .Select(l => l.EntryNumber).Should().BeEquivalentTo([1, 2], "the upper bound is exclusive");
        (await _repo.GetLedgerLinesByAccountNumAsync(40000004, type: "purchase", ct: ct))
            .Should().ContainSingle().Which.EntryNumber.Should().Be(1);
    }

    [HumansFact]
    public async Task Lines_by_entry_numbers_return_every_leg_on_any_account()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedAsync(
            Line(7, 1, 40000004, Now, credit: 121m),
            Line(7, 2, 62900300, Now, debit: 121m),
            Line(8, 1, 57200001, Now, debit: 5m));

        (await _repo.GetLedgerLinesByEntryNumbersAsync([7], ct)).Select(l => l.AccountNum)
            .Should().BeEquivalentTo([40000004, 62900300]);
        (await _repo.GetLedgerLinesByEntryNumbersAsync([], ct)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Type_totals_and_window_sums_are_grouped_in_the_query()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var purchase = Line(1, 1, 40000004, Instant.FromUtc(2026, 3, 1, 0, 0), credit: 100m);
        purchase.Type = "purchase";
        await SeedAsync(
            purchase,
            Line(2, 1, 40000004, Instant.FromUtc(2026, 3, 2, 0, 0), debit: 40m),
            Line(3, 1, 57200001, Instant.FromUtc(2026, 3, 2, 0, 0), credit: 40m));

        var byType = await _repo.GetAccountTypeTotalsAsync(40000000, 40999999, ct);
        byType.Should().BeEquivalentTo(new[]
        {
            new HoldedAccountTypeTotals(40000004, "purchase", 0m, 100m),
            new HoldedAccountTypeTotals(40000004, "payment", 40m, 0m),
        });

        var sums = await _repo.GetSumsByAccountAsync(
            [40000004, 57200001], Instant.FromUtc(2026, 3, 2, 0, 0), Instant.FromUtc(2026, 3, 3, 0, 0), ct);
        sums.Should().BeEquivalentTo(new Dictionary<int, decimal> { [40000004] = 40m, [57200001] = -40m });
    }

    [HumansFact]
    public async Task Replace_keeps_entry_id_and_checked_current()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedAsync(Line(1, 1, 40000004, Now, credit: 1m));

        var resynced = Line(1, 1, 40000004, Now, credit: 1m);
        resynced.EntryId = "68f0c1a2b3c4d5e6f7a8b9c0";
        resynced.Checked = true;
        await SeedAsync(resynced);

        var line = (await _repo.GetLedgerLinesByEntryNumbersAsync([1], ct)).Single();
        line.EntryId.Should().Be("68f0c1a2b3c4d5e6f7a8b9c0");
        line.Checked.Should().BeTrue();
    }

    [HumansFact]
    public async Task GetOrCreateSyncState_creates_then_returns_same_row()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;

        var created = await _repo.GetOrCreateSyncStateAsync(HoldedSyncKind.Ledger, ct);
        created.Kind.Should().Be(HoldedSyncKind.Ledger);
        created.SyncStatus.Should().Be(HoldedSyncStatus.Idle);

        created.SyncStatus = HoldedSyncStatus.Error;
        created.LastError = "57200001: holded 418840.54, local 771074.85";
        created.LastCount = 7;
        await _repo.SaveSyncStateAsync(created, ct);

        var again = await _repo.GetOrCreateSyncStateAsync(HoldedSyncKind.Ledger, ct);
        again.SyncStatus.Should().Be(HoldedSyncStatus.Error);
        again.LastError.Should().Be("57200001: holded 418840.54, local 771074.85");
        again.LastCount.Should().Be(7);

        // One row per kind, and a second kind does not disturb the first.
        await _repo.GetOrCreateSyncStateAsync(HoldedSyncKind.Accounts, ct);
        var states = await _repo.GetSyncStatesAsync(ct);
        states.Select(s => s.Kind).Should().BeEquivalentTo([HoldedSyncKind.Ledger, HoldedSyncKind.Accounts]);
    }

    [HumansFact]
    public async Task ReplaceLedgerWindow_upserts_and_deletes_missing()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;

        var a = Line(1, 1, 40000004, Instant.FromUtc(2026, 6, 5, 0, 0), credit: 100m);
        var b = Line(2, 1, 40000004, Instant.FromUtc(2026, 6, 6, 0, 0), credit: 200m);
        var c = Line(3, 1, 40000004, Instant.FromUtc(2026, 1, 9, 0, 0), credit: 300m);
        await SeedAsync(a, b, c);

        var aPrime = Line(1, 1, 40000004, a.Date, credit: 150m);
        var d = Line(4, 1, 40000004, Instant.FromUtc(2026, 6, 20, 0, 0), debit: 50m);
        await _repo.ReplaceLedgerWindowAsync(WindowFrom, WindowTo, null, [aPrime, d], Now, ct);

        var lines = await AllLinesAsync(ct);
        lines.Select(l => l.EntryNumber).Should().BeEquivalentTo([1, 3, 4]);
        lines.Single(l => l.EntryNumber == 1).Credit.Should().Be(150m);
        lines.Single(l => l.EntryNumber == 3).Credit.Should().Be(300m, "rows outside the window are untouched");
        lines.Single(l => l.EntryNumber == 4).Debit.Should().Be(50m);
    }

    [HumansFact]
    public async Task ReplaceLedgerWindow_scoped_to_account_leaves_other_accounts()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;

        var mine = Line(1, 1, 40000004, Instant.FromUtc(2026, 6, 5, 0, 0), credit: 100m);
        var theirs = Line(2, 1, 57200001, Instant.FromUtc(2026, 6, 6, 0, 0), debit: 200m);
        await SeedAsync(mine, theirs);

        await _repo.ReplaceLedgerWindowAsync(WindowFrom, WindowTo, 40000004, [], Now, ct);

        var lines = await AllLinesAsync(ct);
        lines.Should().ContainSingle();
        lines[0].AccountNum.Should().Be(57200001);
    }

    [HumansFact]
    public async Task ReplaceLedgerWindow_empty_fetch_deletes_everything_in_window()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;

        var inside = Line(1, 1, 40000004, Instant.FromUtc(2026, 6, 5, 0, 0), debit: 23m);
        var outside = Line(2, 1, 40000004, Instant.FromUtc(2026, 1, 9, 0, 0), credit: 300m);
        await SeedAsync(inside, outside);

        await _repo.ReplaceLedgerWindowAsync(WindowFrom, WindowTo, null, [], Now, ct);

        var lines = await AllLinesAsync(ct);
        lines.Should().ContainSingle("an empty sweep is a valid result and clears the window");
        lines[0].EntryNumber.Should().Be(2);
    }

    [HumansFact]
    public async Task ReplaceLedgerWindow_reclassified_line_moves_account_instead_of_duplicating()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;

        await SeedAsync(Line(1, 1, 62900000, Instant.FromUtc(2026, 6, 5, 0, 0), debit: 40m));

        var reclassified = Line(1, 1, 62900128, Instant.FromUtc(2026, 6, 5, 0, 0), debit: 40m);
        await _repo.ReplaceLedgerWindowAsync(WindowFrom, WindowTo, null, [reclassified], Now, ct);

        var lines = await AllLinesAsync(ct);
        lines.Should().ContainSingle();
        lines[0].AccountNum.Should().Be(62900128);
    }

    [HumansFact]
    public async Task HasAnyLedgerLines_is_false_on_a_cold_cache()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;

        (await _repo.HasAnyLedgerLinesAsync(ct)).Should().BeFalse();

        await SeedAsync(Line(1, 1, 40000004, Instant.FromUtc(2026, 6, 5, 0, 0), credit: 100m));

        (await _repo.HasAnyLedgerLinesAsync(ct)).Should().BeTrue();
        (await _repo.GetLedgerLinesByAccountNumAsync(40000004, ct: ct)).Should().ContainSingle();
        (await _repo.GetLedgerLinesByAccountNumAsync(57200001, ct: ct)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task UpsertAccounts_replaces_totals()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;

        await _repo.UpsertAccountsAsync(
            [
                new HoldedAccount { Number = 40000004, HoldedId = "h1", Name = "Peter D", Balance = -53203m },
                new HoldedAccount { Number = 62900000, HoldedId = "h2", Name = "Otros servicios", Balance = 133416.45m },
            ],
            Now, ct);

        await _repo.UpsertAccountsAsync(
            [new HoldedAccount { Number = 40000004, HoldedId = "h1", Name = "Peter Drier", Balance = -1m, Archived = true }],
            Now, ct);

        var accounts = await _repo.GetAccountsAsync(ct);
        accounts.Should().HaveCount(2);
        var updated = accounts.Single(a => a.Number == 40000004);
        updated.Name.Should().Be("Peter Drier");
        updated.Balance.Should().Be(-1m);
        updated.Archived.Should().BeTrue();
        accounts.Single(a => a.Number == 62900000).Balance.Should().Be(133416.45m);
    }

    [HumansFact]
    public async Task AddApiCalls_round_trips()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;

        await _repo.AddApiCallsAsync([], ct);
        (await _repo.GetApiCallsAsync(ct)).Should().BeEmpty();

        await _repo.AddApiCallsAsync(
            [
                new HoldedApiCall
                {
                    Id = Guid.NewGuid(), CalledAt = Now, Endpoint = "ListLedgerEntriesAsync",
                    Method = "GET", StatusCode = 200, RateLimitRemaining = 42, RateLimitWindow = "minute",
                },
            ],
            ct);

        var calls = await _repo.GetApiCallsAsync(ct);
        calls.Should().ContainSingle();
        calls[0].Endpoint.Should().Be("ListLedgerEntriesAsync");
        calls[0].RateLimitRemaining.Should().Be(42);
    }
}
