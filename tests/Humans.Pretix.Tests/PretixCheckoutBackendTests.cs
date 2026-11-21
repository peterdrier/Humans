using System.Net;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Humans.Pretix.Services;
using Humans.Tickets.Contracts;
using NodaTime;
using Xunit;
using static Humans.Pretix.Tests.PretixTestHost;

namespace Humans.Pretix.Tests;

public class PretixCheckoutBackendTests
{
    private static readonly Guid OrderId = Guid.Parse("0193a1b2-0000-7000-8000-000000000001");
    private static readonly Guid EventId = Guid.NewGuid();
    private static readonly string Code = PretixCheckoutBackend.OrderCode(OrderId);
    private static readonly string OrderPath = $"{Api}orders/{Code}/";

    private static CheckoutRequest Request(string culture = "es-ES", string currency = "EUR") => new(
        OrderId, EventId, "nowhere2027", "Bea Buyer", "bea@example.org", currency, culture,
        [
            new CheckoutLine(CheckoutLineKind.Ticket, "Entry", 22500, 0.21m, "Ana Attendee"),
            new CheckoutLine(CheckoutLineKind.Donation, "Donation", 77500, 0m, null),
        ],
        Instant.FromUtc(2026, 11, 21, 1, 0),
        new Uri("https://humans.test/Tickets/Checkout/Return?order=1"));

    private static FakePretix WithEvent(string currency = "EUR") =>
        new FakePretix().On("GET", Api, HttpStatusCode.OK, new { currency });

    [HumansFact]
    public async Task Create_PostsHumansPricesAndReturnsPayLinkBackToHumans()
    {
        var pretix = WithEvent().On("POST", $"{Api}orders/", HttpStatusCode.Created, Order(Code));

        var session = await Create(pretix).CreateCheckoutAsync(Request(), TestContext.Current.CancellationToken);

        session.BackendOrderId.Should().Be($"nowhere2027/{Code}");
        session.AdminUrl!.AbsoluteUri.Should().Be($"https://pretix.test/control/event/nobodies/nowhere2027/orders/{Code}/");
        session.PaymentUrl.AbsoluteUri.Should().Be(
            $"https://pretix.test/nobodies/nowhere2027/order/{Code}/secret/pay/change?return_url="
            + Uri.EscapeDataString("https://humans.test/Tickets/Checkout/Return?order=1"));

        var body = JsonNode.Parse(pretix.BodiesFor("POST", $"{Api}orders/").Single()!)!;
        body["code"]!.GetValue<string>().Should().Be(Code);
        body["locale"]!.GetValue<string>().Should().Be("es");
        body["force"]!.GetValue<bool>().Should().BeTrue();
        body["send_email"]!.GetValue<bool>().Should().BeFalse();
        body["expires"]!.GetValue<string>().Should().Be("2026-11-21T01:00:00Z");
        body["api_meta"]!["humans_order_id"]!.GetValue<string>().Should().Be(OrderId.ToString());
        var positions = body["positions"]!.AsArray();
        positions[0]!["item"]!.GetValue<int>().Should().Be(1);
        positions[0]!["price"]!.GetValue<string>().Should().Be("225.00");
        positions[0]!["attendee_name"]!.GetValue<string>().Should().Be("Ana Attendee");
        positions[1]!["item"]!.GetValue<int>().Should().Be(2);
        positions[1]!["price"]!.GetValue<string>().Should().Be("775.00");
        positions[1]!.AsObject().ContainsKey("attendee_name").Should().BeFalse();
    }

    [HumansFact]
    public async Task Create_RetriedWithSameOrderId_ReturnsTheExistingOrder()
    {
        var pretix = WithEvent()
            .On("POST", $"{Api}orders/", HttpStatusCode.BadRequest, new { code = new[] { "This order code is already in use." } })
            .On("GET", OrderPath, HttpStatusCode.OK, Order(Code, total: 1015.52m, apiMeta: new { humans_order_id = OrderId.ToString() }));

        var session = await Create(pretix).CreateCheckoutAsync(Request(), TestContext.Current.CancellationToken);

        session.BackendOrderId.Should().Be($"nowhere2027/{Code}");
        pretix.BodiesFor("POST", $"{Api}orders/").Should().ContainSingle();
    }

    [HumansFact]
    public async Task Create_CodeOwnedByAnotherOrder_Throws()
    {
        var pretix = WithEvent()
            .On("POST", $"{Api}orders/", HttpStatusCode.BadRequest, new { code = new[] { "This order code is already in use." } })
            .On("GET", OrderPath, HttpStatusCode.OK, Order(Code, apiMeta: new { humans_order_id = Guid.NewGuid().ToString() }));

        var act = () => Create(pretix).CreateCheckoutAsync(Request(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<TicketCheckoutException>();
    }

    [HumansFact]
    public async Task Create_TaxedDifferentlyThanAsked_CancelsTheOrderAndThrows()
    {
        var pretix = WithEvent()
            .On("POST", $"{Api}orders/", HttpStatusCode.Created,
                Order(Code, positions: [Position(1, "225.00", "10.00"), Position(2, "775.00", "0.00")]))
            .On("POST", $"{OrderPath}mark_canceled/", HttpStatusCode.OK, Order(Code, status: "c"));

        var act = () => Create(pretix).CreateCheckoutAsync(Request(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<TicketCheckoutException>().WithMessage("*taxed at 10*");
        pretix.BodiesFor("POST", $"{OrderPath}mark_canceled/").Should().ContainSingle();
    }

    [HumansFact]
    public async Task Create_LanguageTheEventDoesNotOffer_RetriesWithoutLocale()
    {
        var pretix = WithEvent()
            .On("POST", $"{Api}orders/", HttpStatusCode.BadRequest, new { locale = new[] { "\"ca\" is not a valid choice." } })
            .On("POST", $"{Api}orders/", HttpStatusCode.Created, Order(Code));

        await Create(pretix).CreateCheckoutAsync(Request(culture: "ca-ES"), TestContext.Current.CancellationToken);

        var bodies = pretix.BodiesFor("POST", $"{Api}orders/").ToList();
        bodies.Should().HaveCount(2);
        JsonNode.Parse(bodies[1]!)!.AsObject().ContainsKey("locale").Should().BeFalse();
    }

    [HumansFact]
    public async Task Create_CurrencyTheEventDoesNotSell_ThrowsBeforeCreating()
    {
        var pretix = WithEvent("CHF");

        var act = () => Create(pretix).CreateCheckoutAsync(Request(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<TicketCheckoutException>();
        pretix.Requests.Should().ContainSingle();
    }

    [HumansFact]
    public async Task Create_Unconfigured_Throws()
    {
        var settings = PretixTestHost.Settings();
        settings.ApiToken = "";
        var backend = Create(new FakePretix(), settings);

        backend.IsConfigured.Should().BeFalse();
        var act = () => backend.CreateCheckoutAsync(Request(), TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<TicketCheckoutException>();
    }

    [HumansFact]
    public async Task Status_PaidOrder_ReportsMethodFeeAndCompletedRefunds()
    {
        var pretix = new FakePretix().On("GET", OrderPath, HttpStatusCode.OK, Order(
            Code, status: "p", total: 1015.52m, paymentDate: "2026-11-21T00:09:33.022364Z",
            fees: [new { fee_type = "payment", value = "15.52", canceled = false }],
            payments: [new { local_id = 1, state = "confirmed", amount = "1015.52", provider = "stripe_ideal" }],
            refunds:
            [
                new { local_id = 1, state = "done", amount = "225.00", payment = 1, comment = "", provider = "stripe_ideal" },
                new { local_id = 2, state = "failed", amount = "50.00", payment = 1, comment = "", provider = "stripe_ideal" },
            ]));

        var status = await Create(pretix).GetStatusAsync($"nowhere2027/{Code}", TestContext.Current.CancellationToken);

        status.State.Should().Be(CheckoutState.Paid);
        status.FeeMinor.Should().Be(1552);
        status.PaymentMethod.Should().Be("stripe_ideal");
        status.PaidAt.Should().Be(Instant.FromDateTimeOffset(
            DateTimeOffset.Parse("2026-11-21T00:09:33.022364Z", System.Globalization.CultureInfo.InvariantCulture)));
        status.RefundedMinor.Should().Be(22500);
    }

    [HumansTheory]
    [InlineData("n", CheckoutState.Pending)]
    [InlineData("e", CheckoutState.Expired)]
    [InlineData("c", CheckoutState.Cancelled)]
    public async Task Status_MapsPretixOrderStates(string pretixStatus, CheckoutState expected)
    {
        var pretix = new FakePretix().On("GET", OrderPath, HttpStatusCode.OK, Order(Code, status: pretixStatus));

        var status = await Create(pretix).GetStatusAsync($"nowhere2027/{Code}", TestContext.Current.CancellationToken);

        status.State.Should().Be(expected);
        status.PaidAt.Should().BeNull();
        status.FeeMinor.Should().BeNull();
    }

    private static object PaidOrder(params object[] refunds) => Order(
        Code, status: "p",
        payments: [new { local_id = 1, state = "confirmed", amount = "1000.00", provider = "stripe" }],
        refunds: refunds);

    [HumansFact]
    public async Task Refund_RefundsTheExactAmountAgainstThePayment()
    {
        var refundId = Guid.NewGuid();
        var pretix = new FakePretix()
            .On("GET", OrderPath, HttpStatusCode.OK, PaidOrder())
            .On("POST", $"{OrderPath}payments/1/refund/", HttpStatusCode.OK,
                new { local_id = 3, state = "done", amount = "225.00", payment = 1, comment = "", provider = "stripe" });

        var result = await Create(pretix).RefundAsync(
            new RefundRequest(refundId, $"nowhere2027/{Code}", 22500, "Resold"), TestContext.Current.CancellationToken);

        result.State.Should().Be(RefundState.Succeeded);
        result.BackendRefundId.Should().Be("3");
        var body = JsonNode.Parse(pretix.BodiesFor("POST", $"{OrderPath}payments/1/refund/").Single()!)!;
        body["amount"]!.GetValue<string>().Should().Be("225.00");
        body["mark_canceled"]!.GetValue<bool>().Should().BeFalse();
        body["comment"]!.GetValue<string>().Should().StartWith($"humans-refund:{refundId}");
    }

    [HumansFact]
    public async Task Refund_RepeatedRefundId_DoesNotRefundTwice()
    {
        var refundId = Guid.NewGuid();
        var pretix = new FakePretix().On("GET", OrderPath, HttpStatusCode.OK, PaidOrder(
            new { local_id = 3, state = "done", amount = "225.00", payment = 1, comment = $"humans-refund:{refundId} Resold", provider = "stripe" }));

        var result = await Create(pretix).RefundAsync(
            new RefundRequest(refundId, $"nowhere2027/{Code}", 22500, "Resold"), TestContext.Current.CancellationToken);

        result.State.Should().Be(RefundState.Succeeded);
        result.BackendRefundId.Should().Be("3");
        pretix.Requests.Should().ContainSingle();
    }

    [HumansFact]
    public async Task Refund_MethodCannotRefundAutomatically_AsksForAPersonInPretix()
    {
        var pretix = new FakePretix()
            .On("GET", OrderPath, HttpStatusCode.OK, PaidOrder())
            .On("POST", $"{OrderPath}payments/1/refund/", HttpStatusCode.BadRequest,
                new { amount = new[] { "Partial refund not available for this payment method." } });

        var result = await Create(pretix).RefundAsync(
            new RefundRequest(Guid.NewGuid(), $"nowhere2027/{Code}", 22500, "Resold"), TestContext.Current.CancellationToken);

        result.State.Should().Be(RefundState.ManualRequired);
        result.ManualRefundUrl!.AbsoluteUri.Should().Be($"https://pretix.test/control/event/nobodies/nowhere2027/orders/{Code}/");
    }

    [HumansFact]
    public async Task Refund_MoreThanIsLeftOnThePayment_FailsWithoutCallingPretix()
    {
        var pretix = new FakePretix().On("GET", OrderPath, HttpStatusCode.OK, PaidOrder(
            new { local_id = 3, state = "done", amount = "900.00", payment = 1, comment = "", provider = "stripe" }));

        var result = await Create(pretix).RefundAsync(
            new RefundRequest(Guid.NewGuid(), $"nowhere2027/{Code}", 22500, "Resold"), TestContext.Current.CancellationToken);

        result.State.Should().Be(RefundState.Failed);
        pretix.Requests.Should().ContainSingle();
    }

    [HumansFact]
    public async Task Cancel_PaidOrder_Throws()
    {
        var pretix = new FakePretix().On("GET", OrderPath, HttpStatusCode.OK, Order(Code, status: "p"));

        var act = () => Create(pretix).CancelAsync($"nowhere2027/{Code}", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<TicketCheckoutException>();
    }

    [HumansFact]
    public async Task Cancel_CancelledOrder_IsANoOp()
    {
        var pretix = new FakePretix().On("GET", OrderPath, HttpStatusCode.OK, Order(Code, status: "c"));

        await Create(pretix).CancelAsync($"nowhere2027/{Code}", TestContext.Current.CancellationToken);

        pretix.Requests.Should().ContainSingle();
    }

    [HumansTheory]
    [InlineData("n")]
    [InlineData("e")]
    public async Task Cancel_PendingOrExpiredOrder_IsCancelledInPretix(string status)
    {
        var pretix = new FakePretix()
            .On("GET", OrderPath, HttpStatusCode.OK, Order(Code, status: status))
            .On("POST", $"{OrderPath}mark_canceled/", HttpStatusCode.OK, Order(Code, status: "c"));

        await Create(pretix).CancelAsync($"nowhere2027/{Code}", TestContext.Current.CancellationToken);

        pretix.BodiesFor("POST", $"{OrderPath}mark_canceled/").Should().ContainSingle();
    }

    [HumansFact]
    public void OrderCode_IsStablePerOrderAndValidForPretix()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        PretixCheckoutBackend.OrderCode(first).Should().Be(PretixCheckoutBackend.OrderCode(first));
        PretixCheckoutBackend.OrderCode(first).Should().NotBe(PretixCheckoutBackend.OrderCode(second));
        PretixCheckoutBackend.OrderCode(first).Should().HaveLength(16)
            .And.MatchRegex("^[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]+$");
    }
}
