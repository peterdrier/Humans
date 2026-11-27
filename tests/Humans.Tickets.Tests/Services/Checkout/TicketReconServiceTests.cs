using AwesomeAssertions;
using Humans.Testing;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Services.Checkout;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Tickets.Tests.Services.Checkout;

public sealed class TicketReconServiceTests : CheckoutServiceFixture
{
    private readonly IHostEnvironment _environment = Substitute.For<IHostEnvironment>();
    private readonly TicketReconService _recon;

    public TicketReconServiceTests()
    {
        _environment.EnvironmentName.Returns(Environments.Production);
        _recon = new TicketReconService(_repo, [_backend], _environment, _clock, NullLogger<TicketReconService>.Instance);
    }

    [HumansFact]
    public async Task Backend_check_passes_the_trimmed_event_id_and_refuses_an_unknown_key()
    {
        IReadOnlyList<CheckoutBackendFinding> ok = [new(true, "fine")];
        _backend.CheckAsync("slug", Arg.Any<CancellationToken>()).Returns(ok);

        (await _recon.CheckBackendAsync("test", " slug ", TestContext.Current.CancellationToken)).Should().BeSameAs(ok);
        (await _recon.CheckBackendAsync("nope", "slug", TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [HumansFact]
    public async Task Recon_reads_the_backend_a_margin_wider_and_reports_an_unreadable_one_without_comparing()
    {
        _salesEvent.BackendEventRef = " slug ";
        var from = Instant.FromUtc(2027, 1, 1, 0, 0);
        var to = Instant.FromUtc(2027, 1, 2, 0, 0);
        _backend.ListOrdersAsync("slug", from - TicketReconBuilder.Margin, to + TicketReconBuilder.Margin, Arg.Any<CancellationToken>())
            .Returns([new BackendOrderSummary("x", null, from, CheckoutState.Paid, 100, 1, 0, 0)]);

        var recon = await _recon.ReconcileBackendAsync("test", from, to, TestContext.Current.CancellationToken);

        recon!.Problems.Single().Issues.Should().Equal(TicketReconIssue.ExtraAtBackend);
        (await _recon.GetBackendsInUseAsync(TestContext.Current.CancellationToken)).Should().Equal("test");
        (await _recon.ReconcileBackendAsync("nope", from, to, TestContext.Current.CancellationToken)).Should().BeNull();

        _backend.ListOrdersAsync(Arg.Any<string?>(), Arg.Any<Instant>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<BackendOrderSummary>>(_ => throw new HttpRequestException("down"));
        var failed = await _recon.ReconcileBackendAsync("test", from, to, TestContext.Current.CancellationToken);
        failed!.Error.Should().Contain("down");
        failed.Rows.Should().BeEmpty();
        failed.Balanced.Should().BeFalse();
    }

    [HumansFact]
    public async Task Payment_recon_looks_past_the_period_for_a_failed_debit_coming_back()
    {
        var order = new TicketCheckoutOrder
        {
            Id = Guid.NewGuid(),
            SalesEventId = _salesEvent.Id,
            BackendKey = "test",
            State = TicketCheckoutOrderState.Failed,
            TotalMinor = 30000,
            FeeMinor = 450,
            CreatedAt = Now - Duration.FromDays(20),
        };
        _repo.Orders.Add(order);
        var from = Now - Duration.FromDays(30);
        var to = Now - Duration.FromDays(10);
        _backend.GetPaymentLedgerAsync(from, to, Arg.Any<CancellationToken>()).Returns(new PaymentLedger(
            [new PaymentLedgerEntry("txn_c", Now - Duration.FromDays(20), PaymentLedgerEntryKind.Charge, 30000, 450, order.Id, null)], []));
        _backend.GetPaymentLedgerAsync(to, Now, Arg.Any<CancellationToken>()).Returns(new PaymentLedger([], []));
        var ct = TestContext.Current.CancellationToken;

        (await _recon.ReconcilePaymentsAsync("test", from, to, ct))!.Problems.Should().ContainSingle()
            .Which.Issues.Should().Equal(TicketPaymentIssue.NotPaidInHumans);

        _backend.GetPaymentLedgerAsync(to, Now, Arg.Any<CancellationToken>()).Returns(new PaymentLedger(
            [new PaymentLedgerEntry("txn_back", Now - Duration.FromDays(5), PaymentLedgerEntryKind.Charge, -30000, 0, order.Id, null)], []));

        (await _recon.ReconcilePaymentsAsync("test", from, to, ct))!.Balanced.Should().BeTrue();
    }

    [HumansFact]
    public async Task Backend_ledger_flags_orders_open_past_expiry_splits_failed_refunds_and_leaves_free_orders_out_of_paid()
    {
        TicketCheckoutOrder Order(TicketCheckoutOrderState state, string? backendId, Duration age, Duration expiresIn, string key = "test", long total = 1000)
        {
            var order = new TicketCheckoutOrder
            {
                Id = Guid.NewGuid(),
                SalesEventId = _salesEvent.Id,
                BackendKey = key,
                State = state,
                BackendOrderId = backendId,
                CreatedAt = Now - age,
                ExpiresAt = Now + expiresIn,
                TotalMinor = total,
            };
            _repo.Orders.Add(order);
            return order;
        }
        Order(TicketCheckoutOrderState.Created, null, Duration.FromHours(1), Duration.FromHours(1));
        var pastExpiry = Order(TicketCheckoutOrderState.Pending, "cs_1", Duration.FromHours(3), -Duration.FromHours(1));
        Order(TicketCheckoutOrderState.Pending, "cs_2", Duration.FromMinutes(5), Duration.FromMinutes(25));
        Order(TicketCheckoutOrderState.Pending, "cs_3", Duration.FromMinutes(40), -Duration.FromMinutes(10));
        var paid = Order(TicketCheckoutOrderState.Paid, "cs_4", Duration.FromDays(1), -Duration.FromDays(1));
        Order(TicketCheckoutOrderState.Paid, null, Duration.FromDays(1), -Duration.FromDays(1), total: 0);
        var refused = Order(TicketCheckoutOrderState.Paid, "cs_5", Duration.FromDays(1), -Duration.FromDays(1));
        var retried = Order(TicketCheckoutOrderState.Paid, "cs_6", Duration.FromDays(1), -Duration.FromDays(1));
        Order(TicketCheckoutOrderState.Pending, "other_1", Duration.FromDays(2), -Duration.FromDays(2), key: "other");
        _repo.Refunds.Add((Guid.NewGuid(), new TicketCheckoutRefund { Id = Guid.NewGuid(), OrderId = paid.Id, AmountMinor = 1000, State = RefundState.ManualRequired }));
        _repo.Refunds.Add((Guid.NewGuid(), new TicketCheckoutRefund { Id = Guid.NewGuid(), OrderId = paid.Id, AmountMinor = 500, State = RefundState.Succeeded }));
        _repo.Refunds.Add((Guid.NewGuid(), new TicketCheckoutRefund { Id = Guid.NewGuid(), OrderId = refused.Id, AmountMinor = 700, State = RefundState.Failed, CreatedAt = Now }));
        _repo.Refunds.Add((Guid.NewGuid(), new TicketCheckoutRefund { Id = Guid.NewGuid(), OrderId = retried.Id, AmountMinor = 300, State = RefundState.Failed, CreatedAt = Now - Duration.FromHours(2) }));
        _repo.Refunds.Add((Guid.NewGuid(), new TicketCheckoutRefund { Id = Guid.NewGuid(), OrderId = retried.Id, AmountMinor = 300, State = RefundState.Succeeded, CreatedAt = Now - Duration.FromHours(1) }));

        var ledger = await _recon.GetBackendLedgerAsync("test", TestContext.Current.CancellationToken);

        ledger!.Orders.Should().HaveCount(8).And.BeInDescendingOrder(o => o.CreatedAt);
        ledger.Stuck.Should().BeEquivalentTo([pastExpiry]);
        ledger.Paid.Should().BeEquivalentTo([paid, refused, retried]);
        ledger.UnsettledRefunds.Should().ContainSingle().Which.AmountMinor.Should().Be(1000);
        ledger.FailedRefunds.Should().ContainSingle().Which.OrderId.Should().Be(refused.Id);
        (await _recon.GetBackendLedgerAsync("nope", TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [HumansFact]
    public async Task Recon_keeps_reading_a_backend_and_event_its_orders_were_sold_under_after_the_setup_moved()
    {
        var from = Instant.FromUtc(2027, 1, 1, 0, 0);
        var to = Instant.FromUtc(2027, 1, 2, 0, 0);
        _repo.Orders.Add(new TicketCheckoutOrder
        {
            Id = Guid.NewGuid(),
            SalesEventId = _salesEvent.Id,
            BackendKey = "test",
            BackendOrderId = "old/1",
            State = TicketCheckoutOrderState.Paid,
            CreatedAt = from,
        });
        _salesEvent.BackendKey = "elsewhere";
        _backend.EventRefOf("old/1").Returns("old");
        _backend.ListOrdersAsync(Arg.Any<string?>(), Arg.Any<Instant>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>()).Returns([]);

        (await _recon.GetBackendsInUseAsync(TestContext.Current.CancellationToken)).Should().Equal("test");
        var recon = await _recon.ReconcileBackendAsync("test", from, to, TestContext.Current.CancellationToken);

        recon!.Problems.Single().Issues.Should().Equal(TicketReconIssue.MissingAtBackend);
        await _backend.Received(1).ListOrdersAsync("old", Arg.Any<Instant>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }
}
