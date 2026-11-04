using AwesomeAssertions;
using Humans.Stripe.Data;
using Humans.Stripe.Domain;
using Humans.Stripe.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Humans.Stripe.Tests.Mirror;

public sealed class StripeMirrorSyncTests
{
    private static readonly StripeMirrorAccount Tickets = new() { Name = "Tickets", Id = "acct_t", ApiKey = "sk_org_x" };
    private static readonly StripeMirrorAccount Store = new() { Name = "Store", Id = "acct_s", ApiKey = "sk_org_x" };

    private readonly Repository _repo;
    private readonly IStripeMirrorClient _client = Substitute.For<IStripeMirrorClient>();
    private readonly StripeMirrorService _service;

    public StripeMirrorSyncTests()
    {
        var options = new DbContextOptionsBuilder<StripeDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _repo = new Repository(new TestDbContextFactory<StripeDbContext>(options));
        var accounts = new StripeMirrorOptions
        {
            Accounts = [Tickets, Store, new StripeMirrorAccount { Name = "ICE", Id = "acct_i", KeyVariable = "STRIPE_ICE_READ_KEY" }],
        };
        _service = new StripeMirrorService(_repo, _client, Options.Create(accounts),
            new FakeClock(Instant.FromUtc(2026, 11, 5, 12, 0)), NullLogger<StripeMirrorService>.Instance);
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static StripeBalanceTransaction Txn(string id, string account, long net, string category = "charge",
        string? name = null, string? email = null, string? pi = null, string? metadata = null) => new()
        {
            Id = id,
            AccountId = account,
            Type = category,
            ReportingCategory = category,
            Currency = "EUR",
            Amount = net,
            Net = net,
            Created = Instant.FromUtc(2026, 7, 1, 10, 0),
            AvailableOn = Instant.FromUtc(2026, 7, 3, 0, 0),
            CustomerName = name,
            CustomerEmail = email,
            PaymentIntentId = pi,
            Metadata = metadata,
            Raw = $$"""{"id":"{{id}}"}""",
        };

    private void Returns(StripeMirrorAccount account, params StripeBalanceTransaction[] rows) =>
        _client.FetchAsync(account, Arg.Any<CancellationToken>()).Returns(new StripeAccountFetch(rows, [],
            [new StripeBalance { AccountId = account.Id, Currency = "EUR", Available = rows.Sum(r => r.Net) }]));

    [HumansFact]
    public async Task A_sweep_replaces_its_account_and_leaves_the_others()
    {
        Returns(Tickets, Txn("t1", "acct_t", 100), Txn("t2", "acct_t", 200));
        Returns(Store, Txn("s1", "acct_s", 50));
        (await _service.SyncAsync(Ct)).Should().Be(StripeSyncOutcome.Synced);

        Returns(Tickets, Txn("t2", "acct_t", 250));
        _client.FetchAsync(Store, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("Stripe down"));
        (await _service.SyncAsync(Ct)).Should().Be(StripeSyncOutcome.SomeFailed);

        var rows = await _repo.GetTransactionsAsync(Ct);
        rows.Select(t => t.Id).Should().BeEquivalentTo(["t2", "s1"]);
        rows.Single(t => string.Equals(t.Id, "t2", StringComparison.Ordinal)).Net.Should().Be(250);
        var states = await _repo.GetSyncStatesAsync(Ct);
        states.Single(s => string.Equals(s.AccountId, "acct_s", StringComparison.Ordinal)).Should().Match<StripeSyncState>(s =>
            s.SyncStatus == StripeSyncStatus.Error && string.Equals(s.LastError, "Stripe down", StringComparison.Ordinal) && s.LastSyncAt != null);
        states.Single(s => string.Equals(s.AccountId, "acct_t", StringComparison.Ordinal)).LastCount.Should().Be(1);
        states.Should().NotContain(s => string.Equals(s.AccountId, "acct_i", StringComparison.Ordinal), "an account without its key is never swept");
    }

    [HumansFact]
    public async Task No_configured_key_means_no_sweep()
    {
        var service = new StripeMirrorService(_repo, _client,
            Options.Create(new StripeMirrorOptions { Accounts = [new StripeMirrorAccount { Name = "Tickets", Id = "acct_t" }] }),
            new FakeClock(Instant.FromUtc(2026, 11, 5, 12, 0)), NullLogger<StripeMirrorService>.Instance);

        (await service.SyncAsync(Ct)).Should().Be(StripeSyncOutcome.NotConfigured);
        await _client.DidNotReceiveWithAnyArgs().FetchAsync(default!, default);
    }

    [HumansFact]
    public void A_refund_takes_its_buyer_from_the_charge_on_the_same_intent()
    {
        var rows = StripeMirrorService.FillFromSiblings(
        [
            Txn("c", "acct_t", 100, name: "Ana Buyer", email: "ana@example.org", pi: "pi_1"),
            Txn("r", "acct_t", -100, "refund", pi: "pi_1"),
            Txn("x", "acct_t", -5, "refund", pi: "pi_other"),
        ]);

        rows.Single(t => string.Equals(t.Id, "r", StringComparison.Ordinal)).Should().Match<StripeBalanceTransaction>(t =>
            string.Equals(t.CustomerName, "Ana Buyer", StringComparison.Ordinal) && string.Equals(t.CustomerEmail, "ana@example.org", StringComparison.Ordinal));
        rows.Single(t => string.Equals(t.Id, "x", StringComparison.Ordinal)).CustomerName.Should().BeNull();
    }

    [HumansFact]
    public async Task Search_finds_an_order_by_buyer_email_or_metadata_order_id()
    {
        Returns(Tickets,
            Txn("t1", "acct_t", 100, email: "Ana@Example.org", metadata: """{"order_id":"79159110"}"""),
            Txn("t2", "acct_t", 200, name: "Ben"));
        Returns(Store);
        await _service.SyncAsync(Ct);

        (await _service.GetTransactionsPageAsync(new(null, null, "ana@example", null), Ct)).Rows.Select(r => r.Id)
            .Should().Equal("t1");
        (await _service.GetTransactionsPageAsync(new(null, null, "79159110", null), Ct)).Rows.Select(r => r.Id)
            .Should().Equal("t1");
        (await _service.GetTransactionsPageAsync(new("acct_s", null, null, null), Ct)).Rows.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Summary_reconciles_to_the_balance_and_shows_the_reserve_peak()
    {
        var hold = Txn("h", "acct_t", -300, StripeLedger.RiskReserve);
        hold.AvailableOn = Instant.FromUtc(2026, 7, 9, 0, 0);
        var release = Txn("r", "acct_t", 300, StripeLedger.RiskReserve);
        release.AvailableOn = Instant.FromUtc(2026, 7, 20, 0, 0);
        var payout = Txn("p", "acct_t", -900, "payout");
        var minimumHold = Txn("mh", "acct_t", -2500_00, StripeLedger.MinimumBalanceHold);
        var minimumRelease = Txn("mr", "acct_t", 2500_00, "payout_minimum_balance_release");
        _client.FetchAsync(Tickets, Arg.Any<CancellationToken>()).Returns(new StripeAccountFetch(
            [Txn("c", "acct_t", 1000), hold, release, payout, minimumHold, minimumRelease], [],
            [new StripeBalance { AccountId = "acct_t", Currency = "EUR", Available = 90, Pending = 0 }]));
        Returns(Store);
        await _service.SyncAsync(Ct);

        var tickets = (await _service.GetSummaryAsync(Ct)).Accounts.Single(a => string.Equals(a.Id, "acct_t", StringComparison.Ordinal));

        tickets.Computed.Should().Be(1.00m);
        tickets.Difference.Should().Be(-0.10m, "Stripe holds 0.90 against 1.00 computed");
        tickets.PaidOut.Should().Be(9.00m);
        tickets.ReservePeak!.Held.Should().Be(3.00m);
        tickets.ReservePeak.Date.Should().Be(new LocalDate(2026, 7, 9));
        tickets.ReserveHeldNow.Should().Be(0m);
        tickets.MinimumKept!.Held.Should().Be(2500.00m, "the minimum is kept back from the payout and stays in the balance");
    }
}
