using AwesomeAssertions;
using Humans.Audit.Contracts;
using Humans.Finance.Contracts;
using Humans.Holded.Contracts;
using Humans.Store.Contracts;
using Humans.Store.Data;
using Humans.Store.Domain;
using Humans.Store.Services;
using Humans.Stripe.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Store.Tests.Services;

/// <summary>One-shot booking of an issued camp invoice: charges pay the invoice up to its total, the rest is a
/// collection, every gateway line is reconciled, a missing line books nothing, and a rerun is a no-op.</summary>
public class InvoiceBookingTests
{
    private const string Gateway = "gw-1";
    private const string DocId = "doc-1";
    private const int CampAccount = 43_000_001;
    private static readonly Instant FirstCharge = Instant.FromUtc(2026, 6, 1, 10, 15, 30);
    private static readonly Instant SecondCharge = Instant.FromUtc(2026, 6, 3, 18, 0, 5);

    private readonly IStoreRepository _repo = Substitute.For<IStoreRepository>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly IHoldedService _holded = Substitute.For<IHoldedService>();
    private readonly IHoldedWrite _write = Substitute.For<IHoldedWrite>();
    private readonly IStripeService _stripe = Substitute.For<IStripeService>();
    private readonly IFinanceService _finance = Substitute.For<IFinanceService>();
    private readonly List<HoldedBankMovementDto> _feed = [];
    private readonly List<HoldedPaymentDetailDto> _invoicePayments = [];
    private readonly Order _order;
    private readonly Invoice _invoice;
    private readonly InvoiceService _booking;

    public InvoiceBookingTests()
    {
        _order = new Order { Id = Guid.NewGuid(), CampSeasonId = Guid.NewGuid(), Year = 2026, State = OrderState.InvoiceIssued, CounterpartyEmail = "lead@camp.example" };
        _order.IssuedInvoiceId = Guid.NewGuid();
        _invoice = new Invoice
        {
            Id = _order.IssuedInvoiceId.Value,
            OrderId = _order.Id,
            HoldedDocId = DocId,
            HoldedDocNumber = "F260009",
            RequestPayload = """{"kind":"Invoice"}""",
        };
        _repo.GetOrderWithLinesAsync(_order.Id, Arg.Any<CancellationToken>()).Returns(_order);
        _repo.GetInvoicesForOrdersAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns([_invoice]);
        _repo.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(new StoreSettings { StripeGatewayTreasuryId = Gateway });
        _holded.IsConfigured.Returns(true);
        _holded.SyncBankMovementsAsync(default!, default, default).ReturnsForAnyArgs(true);
        _holded.GetBankMovementsAsync(Gateway, Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(_ => new HoldedBankMovementMirror(null, _feed.ToList()));
        _holded.SyncAccountLedgerAsync(CampAccount, Arg.Any<CancellationToken>()).Returns(true);
        _holded.GetAccountBalancesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, decimal> { [CampAccount] = 0m });
        _holded.GetSalesDocumentAsync(HoldedSalesDocumentKind.Invoice, DocId, Arg.Any<CancellationToken>())
            .Returns(_ => new HoldedSalesDocumentDto
            {
                Id = DocId,
                DocNumber = "F260009",
                Subtotal = 82.64m,
                Tax = 17.36m,
                Total = 100m,
                ContactId = "contact-1",
                ContactAccountNum = CampAccount,
                PaymentsPending = 100m - _invoicePayments.Sum(p => p.Amount),
                Payments = _invoicePayments.ToList(),
                Lines = [],
                RawJson = "{}",
            });
        _write.PaySalesDocumentAsync(default, default!, default, default!, default, default).ReturnsForAnyArgs(call =>
        {
            var id = $"pay-{_invoicePayments.Count + 1}";
            _invoicePayments.Add(new HoldedPaymentDetailDto(id, call.ArgAt<decimal>(2), call.ArgAt<LocalDate>(4), call.ArgAt<string>(3)));
            return id;
        });
        _write.CreateTreasuryPaymentAsync(default, default!, default!, default, default, default).ReturnsForAnyArgs("collection-1");
        _write.ReconcileBankMovementAsync(default!, default!, default!).ReturnsForAnyArgs(call =>
        {
            var i = _feed.FindIndex(m => string.Equals(m.Id, call.ArgAt<string>(1), StringComparison.Ordinal));
            _feed[i] = _feed[i] with { Status = "reconciled" };
            return Task.CompletedTask;
        });

        var clock = new FakeClock(Instant.FromUtc(2026, 9, 1, 10, 0));
        var orders = new Service(
            _repo, _audit, Substitute.For<IAuditViewerService>(), Substitute.For<Humans.Camps.Contracts.ICampService>(), Substitute.For<Humans.Teams.Contracts.ITeamService>(), new FakeClock(Instant.FromUtc(2026, 9, 1, 10, 0)), Substitute.For<Humans.Settings.Contracts.ISettingsService>(), _stripe,
            new CatalogService(_repo, _audit, new FakeClock(Instant.FromUtc(2026, 9, 1, 10, 0)), _holded, NullLogger<CatalogService>.Instance), NullLogger<Service>.Instance);
        _booking = new InvoiceService(_repo, _audit, clock, _holded, _write, _stripe, _finance, orders, NullLogger<InvoiceService>.Instance);
    }

    private void Charge(string pi, decimal amount, Instant created, bool fedToHolded = true)
    {
        _order.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(),
            OrderId = _order.Id,
            AmountEur = amount,
            Method = PaymentMethod.Stripe,
            Status = PaymentStatus.Paid,
            StripePaymentIntentId = pi,
            ReceivedAt = created,
        });
        _stripe.GetStoreChargeAsync(pi, Arg.Any<CancellationToken>()).Returns(new StoreChargeData(amount, created));
        if (fedToHolded)
            _feed.Add(new HoldedBankMovementDto
            {
                Id = "line-" + pi,
                Date = created.InUtc().Date,
                BookedAt = created,
                Amount = amount,
                Status = "pending",
            });
    }

    private Task<InvoiceBookingState> BookAsync() => _booking.BookAsync(_order.Id, Guid.NewGuid(), TestContext.Current.CancellationToken);

    [HumansFact]
    public async Task A_charge_crossing_the_total_pays_only_the_rest_and_books_the_excess_as_a_collection()
    {
        Charge("pi_1", 60m, FirstCharge);
        Charge("pi_2", 50m, SecondCharge);

        var state = await BookAsync();

        state.Should().Be(InvoiceBookingState.Booked);
        _invoicePayments.Select(p => p.Amount).Should().Equal(60m, 40m);
        await _write.Received(1).CreateTreasuryPaymentAsync(HoldedTreasuryPaymentKind.Collection, "contact-1", Gateway, 10m,
            new LocalDate(2026, 6, 3), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _write.Received(1).ReconcileBankMovementAsync(Gateway, "line-pi_2",
            Arg.Is<IReadOnlyList<HoldedReconcileDocumentRef>>(r => r.Select(x => x.DocumentId).SequenceEqual(new[] { "pay-2", "collection-1" })),
            Arg.Any<CancellationToken>());
        _feed.Should().OnlyContain(m => m.Status == "reconciled");
        await _write.Received(1).SendSalesDocumentAsync(HoldedSalesDocumentKind.Invoice, DocId,
            Arg.Is<IReadOnlyList<string>?>(e => e != null && e.Single() == "lead@camp.example"), Arg.Any<CancellationToken>());
        _invoice.BookingState.Should().Be(InvoiceBookingState.Booked);
    }

    [HumansFact]
    public async Task A_charge_missing_from_the_feed_books_nothing_and_waits()
    {
        Charge("pi_1", 60m, FirstCharge);
        Charge("pi_2", 40m, SecondCharge, fedToHolded: false);

        var state = await BookAsync();

        state.Should().Be(InvoiceBookingState.WaitingForFeed);
        _invoice.BookingNote.Should().Contain("pi_2");
        await _write.DidNotReceiveWithAnyArgs().PaySalesDocumentAsync(default, default!, default, default!, default, default);
        await _write.DidNotReceiveWithAnyArgs().ReconcileBankMovementAsync(default!, default!, default!);
        await _write.DidNotReceiveWithAnyArgs().SendSalesDocumentAsync(default, default!, default);
    }

    [HumansFact]
    public async Task A_rerun_of_a_booked_order_posts_nothing()
    {
        Charge("pi_1", 100m, FirstCharge);
        await BookAsync();
        _write.ClearReceivedCalls();

        var state = await BookAsync();

        state.Should().Be(InvoiceBookingState.Booked);
        await _write.DidNotReceiveWithAnyArgs().PaySalesDocumentAsync(default, default!, default, default!, default, default);
        await _write.DidNotReceiveWithAnyArgs().CreateTreasuryPaymentAsync(default, default!, default!, default, default, default);
        await _write.DidNotReceiveWithAnyArgs().ReconcileBankMovementAsync(default!, default!, default!);
        await _write.DidNotReceiveWithAnyArgs().SendSalesDocumentAsync(default, default!, default);
    }

    [HumansFact]
    public async Task A_refund_goes_back_to_the_contact_and_its_line_is_reconciled()
    {
        // EUR 110 charged on a EUR 100 invoice, EUR 10 refunded in Stripe.
        Charge("pi_1", 110m, FirstCharge);
        var refundedAt = Instant.FromUtc(2026, 6, 10, 9, 0, 0);
        _order.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(),
            OrderId = _order.Id,
            AmountEur = -10m,
            Method = PaymentMethod.Refund,
            Status = PaymentStatus.Paid,
            ExternalRef = "re_1",
            ReceivedAt = refundedAt,
        });
        // Recorded by hand at entry time: the feed line is found by Stripe's own creation second.
        _order.Payments.Last().ReceivedAt = Instant.FromUtc(2026, 6, 12, 0, 0);
        _stripe.ListStoreRefundsAsync("pi_1", Arg.Any<CancellationToken>())
            .Returns([new StoreRefundData("re_1", "pi_1", 10m, "succeeded", refundedAt)]);
        _feed.Add(new HoldedBankMovementDto { Id = "line-re_1", Date = new LocalDate(2026, 6, 10), BookedAt = refundedAt, Amount = -10m, Status = "pending" });

        var state = await BookAsync();

        state.Should().Be(InvoiceBookingState.Booked);
        await _write.Received(1).CreateTreasuryPaymentAsync(HoldedTreasuryPaymentKind.Payment, "contact-1", Gateway, 10m,
            new LocalDate(2026, 6, 10), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        _feed.Should().OnlyContain(m => m.Status == "reconciled");
    }

    [HumansFact]
    public async Task A_camp_account_off_zero_after_posting_hands_the_order_over_and_does_not_send()
    {
        Charge("pi_1", 100m, FirstCharge);
        _holded.GetAccountBalancesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, decimal> { [CampAccount] = 12.5m });

        var state = await BookAsync();

        state.Should().Be(InvoiceBookingState.NeedsReview);
        _invoice.BookingNote.Should().Contain("12.50");
        await _write.DidNotReceiveWithAnyArgs().SendSalesDocumentAsync(default, default!, default);
    }

    [HumansFact]
    public async Task A_step_failing_after_a_post_hands_the_order_over_and_a_rerun_posts_nothing()
    {
        Charge("pi_1", 110m, FirstCharge);
        _write.WhenForAnyArgs(w => w.ReconcileBankMovementAsync(default!, default!, default!))
            .Do(_ => throw new InvalidOperationException("boom"));

        var state = await BookAsync();

        state.Should().Be(InvoiceBookingState.NeedsReview);
        _invoice.BookingNote.Should().Contain("collection EUR 10.00");
        await _audit.Received(1).LogAsync(StoreAuditActions.StoreInvoiceBooked, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Is<string>(d => d.Contains("Already posted")), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>());
        _write.ClearReceivedCalls();

        (await BookAsync()).Should().Be(InvoiceBookingState.NeedsReview);
        _write.ReceivedCalls().Should().BeEmpty();
    }
}
