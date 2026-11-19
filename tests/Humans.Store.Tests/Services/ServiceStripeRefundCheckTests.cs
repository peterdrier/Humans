using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Camps.Contracts;
using Humans.Holded.Contracts;
using Humans.Settings.Contracts;
using Humans.Store.Contracts;
using Humans.Store.Data;
using Humans.Store.Domain;
using Humans.Store.Services;
using Humans.Store.Services.Dtos;
using Humans.Stripe.Contracts;
using Humans.Teams.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Store.Tests.Services;

public class ServiceStripeRefundCheckTests
{
    private static readonly Instant RefundedAt = Instant.FromUtc(2026, 9, 1, 10, 0);
    private readonly IStoreRepository _repo = Substitute.For<IStoreRepository>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly IStripeService _stripe = Substitute.For<IStripeService>();
    private readonly Service _service;

    public ServiceStripeRefundCheckTests()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetActiveEventSettings().Returns(BurnFixtures.Burn(year: 2026, timeZoneId: "Europe/Madrid"));
        _service = new Service(_repo, _audit, Substitute.For<IAuditViewerService>(), Substitute.For<ICampService>(), Substitute.For<ITeamService>(),
            new FakeClock(Instant.FromUtc(2026, 9, 2, 12, 0)), settings, _stripe, Substitute.For<IHoldedService>(),
            Substitute.For<IHoldedWrite>(), NullLogger<Service>.Instance);
    }

    private Order OrderWith(params Payment[] payments)
    {
        var order = new Order
        {
            Id = Guid.NewGuid(),
            CampSeasonId = Guid.NewGuid(),
            Year = 2026,
            State = OrderState.Open,
            Lines = [],
            Payments = payments.ToList(),
        };
        _repo.GetOrderWithLinesAndPaymentsAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);
        return order;
    }

    private static Payment StripePaid(string pi, decimal amount) => new()
    {
        Id = Guid.NewGuid(),
        AmountEur = amount,
        Method = PaymentMethod.Stripe,
        Status = PaymentStatus.Paid,
        StripePaymentIntentId = pi,
    };

    private static StoreRefundData Refund(string id, string pi, decimal amount, string status = "succeeded") =>
        new(id, pi, amount, status, RefundedAt);

    [HumansFact]
    public async Task Books_each_refund_once_with_pending_ones_counted_at_once()
    {
        var actor = Guid.NewGuid();
        var alreadyBooked = new Payment
        {
            Id = Guid.NewGuid(),
            AmountEur = -20m,
            Method = PaymentMethod.Refund,
            Status = PaymentStatus.Paid,
            ExternalRef = "re_old",
        };
        var order = OrderWith(StripePaid("pi_a", 500m), alreadyBooked);
        _stripe.ListStoreRefundsAsync("pi_a", Arg.Any<CancellationToken>()).Returns(new List<StoreRefundData>
        {
            Refund("re_old", "pi_a", 20m),
            Refund("re_new", "pi_a", 120.50m),
            Refund("re_wait", "pi_a", 10m, "pending"),
            Refund("re_failed", "pi_a", 5m, "failed"),
        });

        var result = await _service.RecordStripeRefundsAsync(order.Id, actor, TestContext.Current.CancellationToken);

        result.Should().Be(new StripeRefundCheckResult(2, 130.50m, 1, 0, 0));
        await _repo.Received(2).AddPaymentAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).AddPaymentAsync(Arg.Is<Payment>(p =>
            p.OrderId == order.Id && p.AmountEur == -120.50m && p.Method == PaymentMethod.Refund
            && p.MethodName == PaymentMethod.Refund && p.Status == PaymentStatus.Paid && p.ExternalRef == "re_new"
            && p.StripePaymentIntentId == null && p.ReceivedAt == RefundedAt && p.RecordedByUserId == actor),
            Arg.Any<CancellationToken>());
        await _repo.Received(1).AddPaymentAsync(Arg.Is<Payment>(p =>
            p.AmountEur == -10m && p.Status == PaymentStatus.Pending && p.ExternalRef == "re_wait"),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(StoreAuditActions.StorePaymentRecorded, AuditEntityTypes.Payment,
            Arg.Any<Guid>(), Arg.Is<string>(s => s.Contains("re_new")), actor, order.Id, AuditEntityTypes.Order);
        await _repo.DidNotReceive().UpdatePaymentStatusAsync(Arg.Any<Guid>(), Arg.Any<PaymentStatus>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Booked_refunds_follow_stripe_forward_only()
    {
        Payment Booked(string re, PaymentStatus status) => new()
        {
            Id = Guid.NewGuid(),
            AmountEur = -50m,
            Method = PaymentMethod.Refund,
            Status = status,
            ExternalRef = re,
        };
        var settles = Booked("re_settles", PaymentStatus.Pending);
        var fails = Booked("re_fails", PaymentStatus.Pending);
        var reversed = Booked("re_reversed", PaymentStatus.Paid);
        var stale = Booked("re_stale", PaymentStatus.Paid);
        var order = OrderWith(StripePaid("pi_a", 500m), settles, fails, reversed, stale);
        _stripe.ListStoreRefundsAsync("pi_a", Arg.Any<CancellationToken>()).Returns(new List<StoreRefundData>
        {
            Refund("re_settles", "pi_a", 50m),
            Refund("re_fails", "pi_a", 50m, "canceled"),
            Refund("re_reversed", "pi_a", 50m, "failed"),
            Refund("re_stale", "pi_a", 50m, "pending"),
        });

        var result = await _service.RecordStripeRefundsAsync(order.Id, Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Should().Be(new StripeRefundCheckResult(0, 0m, 0, 1, 2));
        await _repo.Received(1).UpdatePaymentStatusAsync(settles.Id, PaymentStatus.Paid, Arg.Any<CancellationToken>());
        await _repo.Received(1).UpdatePaymentStatusAsync(fails.Id, PaymentStatus.Failed, Arg.Any<CancellationToken>());
        await _repo.Received(1).UpdatePaymentStatusAsync(reversed.Id, PaymentStatus.Failed, Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().UpdatePaymentStatusAsync(stale.Id, Arg.Any<PaymentStatus>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().AddPaymentAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Refuses_and_books_nothing_when_any_payment_refunds_cannot_be_read()
    {
        var order = OrderWith(StripePaid("pi_a", 100m), StripePaid("pi_b", 100m));
        _stripe.ListStoreRefundsAsync("pi_a", Arg.Any<CancellationToken>())
            .Returns(new List<StoreRefundData> { Refund("re_a", "pi_a", 50m) });
        _stripe.ListStoreRefundsAsync("pi_b", Arg.Any<CancellationToken>()).Returns((IReadOnlyList<StoreRefundData>?)null);

        var result = await _service.RecordStripeRefundsAsync(order.Id, Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Refusal.Should().NotBeNull();
        await _repo.DidNotReceive().AddPaymentAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Only_paid_stripe_payments_are_checked()
    {
        var pending = StripePaid("pi_pending", 100m);
        pending.Status = PaymentStatus.Pending;
        var order = OrderWith(pending);

        var result = await _service.RecordStripeRefundsAsync(order.Id, Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Refusal.Should().NotBeNull();
        await _stripe.DidNotReceive().ListStoreRefundsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
