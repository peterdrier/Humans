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

    private static FakePretix WithEvent(string currency = "EUR", string donationItemName = "humans-donation") =>
        new FakePretix()
            .On("GET", Api, HttpStatusCode.OK, new { currency })
            .On("GET", $"{Api}items/", HttpStatusCode.OK, new
            {
                results = new object[]
                {
                    new { id = 7, internal_name = "humans-ticket" },
                    new { id = 8, internal_name = donationItemName },
                    new { id = 9, internal_name = (string?)null },
                },
            });

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
        positions[0]!["item"]!.GetValue<int>().Should().Be(7);
        positions[0]!["price"]!.GetValue<string>().Should().Be("225.00");
        positions[1]!["item"]!.GetValue<int>().Should().Be(8);
        positions[1]!["price"]!.GetValue<string>().Should().Be("775.00");
        // Names stay in Humans, where an erasure reaches them.
        pretix.BodiesFor("POST", $"{Api}orders/").Single().Should().NotContain("Ana Attendee").And.NotContain("Bea Buyer");
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
    public async Task Create_EventWithoutCarrierItems_ThrowsBeforeCreating()
    {
        var pretix = WithEvent(donationItemName: "something-else");

        var act = () => Create(pretix).CreateCheckoutAsync(Request(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<TicketCheckoutException>().WithMessage("*humans-donation*");
        pretix.BodiesFor("POST", $"{Api}orders/").Should().BeEmpty();
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
    public async Task Status_PaidOrder_ReportsMethodAndCompletedRefunds()
    {
        var pretix = new FakePretix().On("GET", OrderPath, HttpStatusCode.OK, Order(
            Code, status: "p", total: 1015.52m,
            fees: [new { fee_type = "payment", value = "15.52", canceled = false }],
            payments: [new { local_id = 1, state = "confirmed", amount = "1015.52", provider = "stripe_ideal", payment_date = "2026-11-21T00:09:33.022364Z" }],
            refunds:
            [
                new { local_id = 1, state = "done", amount = "225.00", payment = 1, comment = "", provider = "stripe_ideal" },
                new { local_id = 2, state = "failed", amount = "50.00", payment = 1, comment = "", provider = "stripe_ideal" },
            ]));

        var status = await Create(pretix).GetStatusAsync($"nowhere2027/{Code}", TestContext.Current.CancellationToken);

        status.State.Should().Be(CheckoutState.Paid);
        // What Stripe kept is unknown to pretix; the 15.52 is a surcharge, which the port forbids.
        status.FeeMinor.Should().BeNull();
        status.RefundFailed.Should().BeTrue();
        status.PaymentMethod.Should().Be("stripe_ideal");
        status.PaidAt.Should().Be(Instant.FromUtc(2026, 11, 21, 0, 9, 33).PlusTicks(223_640));
        status.RefundedMinor.Should().Be(22500);
    }

    [HumansFact]
    public async Task Status_UnreadablePaymentDate_ThrowsSoTicketsRetries()
    {
        var pretix = new FakePretix().On("GET", OrderPath, HttpStatusCode.OK, Order(
            Code, status: "p", total: 1000m,
            payments: [new { local_id = 1, state = "confirmed", amount = "1000.00", provider = "stripe_ideal", payment_date = "21/11/2026" }]));

        var act = () => Create(pretix).GetStatusAsync($"nowhere2027/{Code}", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HttpRequestException>();
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

    [HumansFact]
    public async Task Status_AfterAFullRefund_KeepsTheRealPaymentMethod()
    {
        var pretix = new FakePretix().On("GET", OrderPath, HttpStatusCode.OK, Order(
            Code, status: "p", total: 0m,
            payments:
            [
                new { local_id = 1, state = "refunded", amount = "225.00", provider = "stripe_ideal", payment_date = "2026-11-21T00:09:33Z" },
                new { local_id = 2, state = "confirmed", amount = "0.00", provider = "free", payment_date = "2026-11-22T10:00:00Z" },
            ],
            refunds: [new { local_id = 1, state = "done", amount = "225.00", payment = 1, comment = "", provider = "stripe_ideal" }]));

        var status = await Create(pretix).GetStatusAsync($"nowhere2027/{Code}", TestContext.Current.CancellationToken);

        status.PaymentMethod.Should().Be("stripe_ideal");
        status.RefundedMinor.Should().Be(22500);
    }

    [HumansTheory]
    [InlineData("n")]
    [InlineData("e")]
    public async Task Status_OrderItsPaymentsCover_IsPaid(string pretixStatus)
    {
        // A payment confirmed after expiry, or an order a refund made in pretix reopened.
        var pretix = new FakePretix().On("GET", OrderPath, HttpStatusCode.OK, Order(
            Code, status: pretixStatus, total: 1000m,
            payments: [new { local_id = 1, state = "confirmed", amount = "1000.00", provider = "stripe_ideal", payment_date = "2026-11-21T00:09:33Z" }]));

        var status = await Create(pretix).GetStatusAsync($"nowhere2027/{Code}", TestContext.Current.CancellationToken);

        status.State.Should().Be(CheckoutState.Paid);
        status.PaidAt.Should().NotBeNull();
    }

    [HumansFact]
    public async Task Status_RetryAfterAFailedRefund_IsNotReportedFailed()
    {
        var pretix = new FakePretix().On("GET", OrderPath, HttpStatusCode.OK, PaidOrder(
            new { local_id = 1, state = "failed", amount = "225.00", payment = 1, comment = "", provider = "stripe" },
            new { local_id = 2, state = "transit", amount = "225.00", payment = 1, comment = "", provider = "stripe" }));

        var status = await Create(pretix).GetStatusAsync($"nowhere2027/{Code}", TestContext.Current.CancellationToken);

        status.RefundFailed.Should().BeFalse();
        status.RefundedMinor.Should().Be(0);
    }

    [HumansFact]
    public async Task Status_RefundMadeInTheStripeDashboard_CountsAsRefundedBeforeAnyoneProcessesIt()
    {
        var pretix = new FakePretix().On("GET", OrderPath, HttpStatusCode.OK, PaidOrder(
            new { local_id = 1, state = "external", source = "external", amount = "40.00", payment = 1, comment = (string?)null, provider = "stripe", details = new { id = "re_3Q" } }));

        var status = await Create(pretix).GetStatusAsync($"nowhere2027/{Code}", TestContext.Current.CancellationToken);

        status.State.Should().Be(CheckoutState.Paid);
        status.RefundedMinor.Should().Be(4000);
        status.DisputedMinor.Should().Be(0);
    }

    [HumansFact]
    public async Task Status_Chargeback_IsDisputedNotRefundedUntilCancelledInPretix()
    {
        var pretix = new FakePretix().On("GET", OrderPath, HttpStatusCode.OK, PaidOrder(
            new { local_id = 1, state = "done", source = "admin", amount = "100.00", payment = 1, comment = "humans-refund:x", provider = "stripe", details = new { id = "re_1" } },
            new { local_id = 2, state = "external", source = "external", amount = "300.00", payment = 1, comment = (string?)null, provider = "stripe", details = new { id = "du_1" } },
            new { local_id = 3, state = "done", source = "external", amount = "200.00", payment = 1, comment = (string?)null, provider = "stripe", details = new { id = "dp_2" } },
            new { local_id = 4, state = "canceled", source = "external", amount = "400.00", payment = 1, comment = (string?)null, provider = "stripe", details = new { id = "du_3" } }));

        var status = await Create(pretix).GetStatusAsync($"nowhere2027/{Code}", TestContext.Current.CancellationToken);

        status.DisputedMinor.Should().Be(50000, "an open and a processed chargeback hold money; a cancelled one was won");
        status.RefundedMinor.Should().Be(10000);
        status.RefundFailed.Should().BeFalse("cancelling a won chargeback is not a failed refund");
    }

    [HumansFact]
    public async Task Status_CancelledOrderRefundedInTheStripeDashboard_StaysCancelled()
    {
        var pretix = new FakePretix().On("GET", OrderPath, HttpStatusCode.OK, Order(
            Code, status: "c", total: 1000m,
            payments: [new { local_id = 1, state = "confirmed", amount = "1000.00", provider = "stripe", payment_date = "2026-11-21T00:09:33Z" }],
            refunds: [new { local_id = 1, state = "external", source = "external", amount = "1000.00", payment = 1, comment = (string?)null, provider = "stripe", details = new { id = "re_1" } }]));

        var status = await Create(pretix).GetStatusAsync($"nowhere2027/{Code}", TestContext.Current.CancellationToken);

        status.State.Should().Be(CheckoutState.Cancelled, "the money has already gone back");
        status.RefundedMinor.Should().Be(100000);
    }

    [HumansFact]
    public async Task Status_CancelledOrderHoldingMoney_IsPaid()
    {
        var pretix = new FakePretix().On("GET", OrderPath, HttpStatusCode.OK, Order(
            Code, status: "c", total: 1000m,
            payments: [new { local_id = 1, state = "confirmed", amount = "1000.00", provider = "stripe_klarna", payment_date = "2026-11-21T00:09:33Z" }]));

        var status = await Create(pretix).GetStatusAsync($"nowhere2027/{Code}", TestContext.Current.CancellationToken);

        status.State.Should().Be(CheckoutState.Paid);
    }

    [HumansFact]
    public async Task Cancel_ExpiredOrderALatePaymentCovers_Throws()
    {
        var pretix = new FakePretix().On("GET", OrderPath, HttpStatusCode.OK, Order(
            Code, status: "e", total: 1000m,
            payments: [new { local_id = 1, state = "confirmed", amount = "1000.00", provider = "stripe_ideal" }]));

        var cancel = () => Create(pretix).CancelAsync($"nowhere2027/{Code}", TestContext.Current.CancellationToken);

        await cancel.Should().ThrowAsync<TicketCheckoutException>();
        pretix.Requests.Should().ContainSingle();
    }

    [HumansFact]
    public async Task Erase_BuyersOrder_ClearsEmailAndAnyAttendeeData()
    {
        var order = new
        {
            code = Code,
            status = "p",
            email = "Bea@Example.org",
            url = "https://pretix.test/x/",
            total = "225.00",
            positions = new object[]
            {
                new { id = 41, positionid = 1, price = "225.00", tax_rate = "21.00", canceled = false, attendee_name = "Ana", attendee_email = (string?)null },
                new { id = 42, positionid = 2, price = "0.00", tax_rate = "0.00", canceled = false, attendee_name = (string?)null, attendee_email = (string?)null },
            },
            fees = Array.Empty<object>(),
            payments = Array.Empty<object>(),
            refunds = Array.Empty<object>(),
        };
        var pretix = new FakePretix()
            .On("GET", OrderPath, HttpStatusCode.OK, order)
            .On("PATCH", $"{Api}orderpositions/41/", HttpStatusCode.OK, new { })
            .On("PATCH", OrderPath, HttpStatusCode.OK, new { });

        await Create(pretix).ErasePersonalDataAsync($"nowhere2027/{Code}", "bea@example.org", TestContext.Current.CancellationToken);

        JsonNode.Parse(pretix.BodiesFor("PATCH", OrderPath).Single()!)!["email"].Should().BeNull();
        var position = JsonNode.Parse(pretix.BodiesFor("PATCH", $"{Api}orderpositions/41/").Single()!)!.AsObject();
        position.ContainsKey("attendee_name").Should().BeTrue();
        position["attendee_name"].Should().BeNull();
        pretix.BodiesFor("PATCH", $"{Api}orderpositions/42/").Should().BeEmpty();
    }

    [HumansFact]
    public async Task Erase_AlreadyErased_DoesNothing()
    {
        var pretix = new FakePretix().On("GET", OrderPath, HttpStatusCode.OK, Order(Code, status: "p"));

        await Create(pretix).ErasePersonalDataAsync($"nowhere2027/{Code}", "bea@example.org", TestContext.Current.CancellationToken);

        pretix.Requests.Should().ContainSingle();
    }

    private static object PaidOrder(params object[] refunds) => Order(
        Code, status: "p",
        payments: [new { local_id = 1, state = "confirmed", amount = "1000.00", provider = "stripe" }],
        refunds: refunds);

    private static object RefundFee(Guid refundId, bool canceled = false) =>
        new { id = 7, fee_type = "other", value = "-225.00", canceled, internal_type = $"humans-refund:{refundId}" };

    private static object PaidOrderWithFee(Guid refundId) => Order(
        Code, status: "p", total: 775m,
        fees: [RefundFee(refundId)],
        payments: [new { local_id = 1, state = "confirmed", amount = "1000.00", provider = "stripe" }]);

    private static FakePretix RefundablePretix(Guid refundId) => new FakePretix()
        .On("GET", OrderPath, HttpStatusCode.OK, PaidOrder())
        .On("POST", $"{OrderPath}change/", HttpStatusCode.OK, PaidOrderWithFee(refundId));

    [HumansFact]
    public async Task Refund_RefundsTheExactAmountAgainstThePayment()
    {
        var refundId = Guid.NewGuid();
        var pretix = RefundablePretix(refundId)
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

        // The order total drops first, so pretix keeps the order paid instead of asking for the refunded amount.
        pretix.Requests.Select(r => r.Route).Should().ContainInOrder($"POST {OrderPath}change/", $"POST {OrderPath}payments/1/refund/");
        var fee = JsonNode.Parse(pretix.BodiesFor("POST", $"{OrderPath}change/").Single()!)!["create_fees"]![0]!;
        fee["value"]!.GetValue<string>().Should().Be("-225.00");
        fee["internal_type"]!.GetValue<string>().Should().Be($"humans-refund:{refundId}");
    }

    [HumansFact]
    public async Task Refund_RetryAfterTheFeeWasAdded_DoesNotLowerTheTotalTwice()
    {
        var refundId = Guid.NewGuid();
        var pretix = new FakePretix()
            .On("GET", OrderPath, HttpStatusCode.OK, PaidOrderWithFee(refundId))
            .On("POST", $"{OrderPath}payments/1/refund/", HttpStatusCode.OK,
                new { local_id = 3, state = "done", amount = "225.00", payment = 1, comment = "", provider = "stripe" });

        var result = await Create(pretix).RefundAsync(
            new RefundRequest(refundId, $"nowhere2027/{Code}", 22500, "Resold"), TestContext.Current.CancellationToken);

        result.State.Should().Be(RefundState.Succeeded);
        pretix.BodiesFor("POST", $"{OrderPath}change/").Should().BeEmpty();
    }

    [HumansFact]
    public async Task Refund_PretixServerError_ThrowsAndTheRetryReusesTheFee()
    {
        var refundId = Guid.NewGuid();
        var pretix = RefundablePretix(refundId)
            .On("POST", $"{OrderPath}payments/1/refund/", HttpStatusCode.BadGateway, "");

        var first = () => Create(pretix).RefundAsync(
            new RefundRequest(refundId, $"nowhere2027/{Code}", 22500, "Resold"), TestContext.Current.CancellationToken);
        await first.Should().ThrowAsync<HttpRequestException>();

        // Tickets retries under the same id: the fee is found, not added again, and the refund goes through.
        var retry = new FakePretix()
            .On("GET", OrderPath, HttpStatusCode.OK, PaidOrderWithFee(refundId))
            .On("POST", $"{OrderPath}payments/1/refund/", HttpStatusCode.OK,
                new { local_id = 3, state = "done", amount = "225.00", payment = 1, comment = "", provider = "stripe" });
        var result = await Create(retry).RefundAsync(
            new RefundRequest(refundId, $"nowhere2027/{Code}", 22500, "Resold"), TestContext.Current.CancellationToken);

        result.State.Should().Be(RefundState.Succeeded);
        retry.BodiesFor("POST", $"{OrderPath}change/").Should().BeEmpty();
    }

    [HumansFact]
    public async Task Refund_PretixRefusesTheCall_FailsAndRestoresTheTotal()
    {
        var refundId = Guid.NewGuid();
        var pretix = RefundablePretix(refundId)
            .On("POST", $"{OrderPath}payments/1/refund/", HttpStatusCode.Forbidden, "{\"detail\": \"no permission\"}");

        var result = await Create(pretix).RefundAsync(
            new RefundRequest(refundId, $"nowhere2027/{Code}", 22500, "Resold"), TestContext.Current.CancellationToken);

        result.State.Should().Be(RefundState.Failed);
        var cancel = JsonNode.Parse(pretix.BodiesFor("POST", $"{OrderPath}change/").Last()!)!;
        cancel["cancel_fees"]![0]!["fee"]!.GetValue<int>().Should().Be(7);
    }

    [HumansTheory]
    [InlineData("{\"detail\": \"External error: card_declined\"}")]
    [InlineData("{\"detail\": \"Invalid state of payment.\"}")]
    [InlineData("{\"amount\": [\"Invalid refund amount, only 100.00 are available to refund.\"]}")]
    public async Task Refund_PretixDeclines_FailsAndRestoresTheTotal(string declined)
    {
        var refundId = Guid.NewGuid();
        var pretix = RefundablePretix(refundId)
            .On("POST", $"{OrderPath}payments/1/refund/", HttpStatusCode.BadRequest, declined);

        var result = await Create(pretix).RefundAsync(
            new RefundRequest(refundId, $"nowhere2027/{Code}", 22500, "Resold"), TestContext.Current.CancellationToken);

        result.State.Should().Be(RefundState.Failed);
        var cancel = JsonNode.Parse(pretix.BodiesFor("POST", $"{OrderPath}change/").Last()!)!;
        cancel["cancel_fees"]![0]!["fee"]!.GetValue<int>().Should().Be(7);
    }

    [HumansFact]
    public async Task Refund_RetryDeclinedAfterAnUnansweredAttempt_RestoresTheTotal()
    {
        var refundId = Guid.NewGuid();
        var pretix = new FakePretix()
            .On("GET", OrderPath, HttpStatusCode.OK, PaidOrderWithFee(refundId))
            .On("POST", $"{OrderPath}payments/1/refund/", HttpStatusCode.BadRequest, "{\"detail\": \"External error: card_declined\"}")
            .On("POST", $"{OrderPath}change/", HttpStatusCode.OK, PaidOrder());

        var result = await Create(pretix).RefundAsync(
            new RefundRequest(refundId, $"nowhere2027/{Code}", 22500, "Resold"), TestContext.Current.CancellationToken);

        result.State.Should().Be(RefundState.Failed);
        var cancel = JsonNode.Parse(pretix.BodiesFor("POST", $"{OrderPath}change/").Single()!)!;
        cancel["cancel_fees"]![0]!["fee"]!.GetValue<int>().Should().Be(7);
    }

    [HumansFact]
    public async Task Refund_DeclinedAfterARefundByHand_KeepsTheFee()
    {
        var refundId = Guid.NewGuid();
        var pretix = new FakePretix()
            .On("GET", OrderPath, HttpStatusCode.OK, Order(
                Code, status: "p", total: 775m,
                fees: [RefundFee(refundId)],
                payments: [new { local_id = 1, state = "confirmed", amount = "1000.00", provider = "stripe" }],
                refunds: [new { local_id = 2, state = "done", amount = "225.00", payment = 1, comment = "", provider = "manual" }]))
            .On("POST", $"{OrderPath}payments/1/refund/", HttpStatusCode.BadRequest, "{\"detail\": \"Invalid state of payment.\"}");

        var result = await Create(pretix).RefundAsync(
            new RefundRequest(refundId, $"nowhere2027/{Code}", 22500, "Resold"), TestContext.Current.CancellationToken);

        result.State.Should().Be(RefundState.Failed);
        pretix.BodiesFor("POST", $"{OrderPath}change/").Should().BeEmpty();
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
    public async Task Refund_RepeatAfterTheRefundFailed_AnswersFailedAndNeverRefundsAgain()
    {
        var refundId = Guid.NewGuid();
        var pretix = new FakePretix()
            .On("GET", OrderPath, HttpStatusCode.OK, Order(
                Code, status: "p", total: 775m,
                fees: [RefundFee(refundId)],
                payments: [new { local_id = 1, state = "confirmed", amount = "1000.00", provider = "stripe" }],
                refunds: [new { local_id = 3, state = "failed", amount = "225.00", payment = 1, comment = $"humans-refund:{refundId} Resold", provider = "stripe" }]))
            .On("POST", $"{OrderPath}change/", HttpStatusCode.OK, PaidOrder());

        var result = await Create(pretix).RefundAsync(
            new RefundRequest(refundId, $"nowhere2027/{Code}", 22500, "Resold"), TestContext.Current.CancellationToken);

        result.State.Should().Be(RefundState.Failed);
        result.BackendRefundId.Should().Be("3");
        pretix.BodiesFor("POST", $"{OrderPath}payments/1/refund/").Should().BeEmpty();
        JsonNode.Parse(pretix.BodiesFor("POST", $"{OrderPath}change/").Single()!)!["cancel_fees"]![0]!["fee"]!.GetValue<int>().Should().Be(7);
    }

    [HumansFact]
    public async Task Refund_MethodCannotRefundAutomatically_AsksForAPersonInPretix()
    {
        var refundId = Guid.NewGuid();
        var pretix = RefundablePretix(refundId)
            .On("POST", $"{OrderPath}payments/1/refund/", HttpStatusCode.BadRequest,
                new { amount = new[] { "Partial refund not available for this payment method." } });

        var result = await Create(pretix).RefundAsync(
            new RefundRequest(refundId, $"nowhere2027/{Code}", 22500, "Resold"), TestContext.Current.CancellationToken);

        result.State.Should().Be(RefundState.ManualRequired);
        result.ManualRefundUrl!.AbsoluteUri.Should().Be($"https://pretix.test/control/event/nobodies/nowhere2027/orders/{Code}/");
        // The lowered total stays, so pretix shows exactly this amount as owed back to the buyer.
        pretix.BodiesFor("POST", $"{OrderPath}change/").Should().ContainSingle();
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
    public async Task Status_PendingOrderWithASettlingPayment_IsProcessingAndCannotBeCancelled()
    {
        var pretix = new FakePretix().On("GET", OrderPath, HttpStatusCode.OK, Order(
            Code, status: "n", total: 1000m,
            payments:
            [
                new { local_id = 1, state = "created", amount = "1000.00", provider = "banktransfer", payment_date = (string?)null },
                new { local_id = 2, state = "pending", amount = "1000.00", provider = "stripe_sepa_debit", payment_date = (string?)null },
            ]));
        var backend = Create(pretix);

        var status = await backend.GetStatusAsync($"nowhere2027/{Code}", TestContext.Current.CancellationToken);
        var cancel = () => backend.CancelAsync($"nowhere2027/{Code}", TestContext.Current.CancellationToken);

        status.State.Should().Be(CheckoutState.Processing);
        await cancel.Should().ThrowAsync<TicketCheckoutException>();
        pretix.Requests.Should().NotContain(r => r.Route.Contains("mark_canceled", StringComparison.Ordinal));
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
    [HumansFact]
    public async Task ListOrders_FollowsPagesCountsTicketItemsAndAddsBackRefundFees()
    {
        object Listed(string code, string datetime, object apiMeta, object[] fees, object[] refunds, decimal total) => new
        {
            code,
            status = "p",
            url = $"https://pretix.test/nobodies/nowhere2027/order/{code}/secret/",
            total = total.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            datetime,
            positions = new object[]
            {
                new { id = 1, positionid = 1, item = 7, price = "225.00", tax_rate = "10.00", canceled = false },
                new { id = 2, positionid = 2, item = 7, price = "0.00", tax_rate = "10.00", canceled = false },
                new { id = 3, positionid = 3, item = 8, price = "75.00", tax_rate = "0.00", canceled = false },
            },
            fees,
            payments = new object[] { new { local_id = 1, state = "confirmed", amount = "300.00", provider = "stripe", payment_date = datetime } },
            refunds,
            api_meta = apiMeta,
        };
        var pretix = new FakePretix()
            .On("GET", $"{Api}items/", HttpStatusCode.OK, new
            {
                results = new object[] { new { id = 7, internal_name = "humans-ticket" }, new { id = 8, internal_name = "humans-donation" } },
            })
            .On("GET", $"{Api}orders/", HttpStatusCode.OK, new
            {
                next = $"https://pretix.test{Api}orders/?page=2",
                results = new[] { Listed(Code, "2027-01-05T09:30:00.123456+00:00", new { humans_order_id = OrderId.ToString() }, [], [], 300m) },
            })
            .On("GET", $"{Api}orders/", HttpStatusCode.OK, new
            {
                next = (string?)null,
                results = new[]
                {
                    Listed("MANUAL1", "2027-01-06T10:00:00+00:00", new { },
                        [new { id = 4, fee_type = "other", value = "-50.00", canceled = false, internal_type = "humans-refund:x" }],
                        [new { local_id = 1, state = "done", amount = "50.00", payment = 1, comment = "humans-refund:x", provider = "stripe" }],
                        250m),
                },
            });

        var orders = await Create(pretix).ListOrdersAsync(
            "nowhere2027", Instant.FromUtc(2027, 1, 1, 0, 0), Instant.FromUtc(2027, 2, 1, 0, 0), TestContext.Current.CancellationToken);

        orders.Should().HaveCount(2);
        orders[0].Should().Be(new BackendOrderSummary(
            $"nowhere2027/{Code}", OrderId, Instant.FromUtc(2027, 1, 5, 9, 30).PlusTicks(1234560), CheckoutState.Paid, 30000, 2, 0, 0));
        orders[1].HumansOrderId.Should().BeNull();
        orders[1].TotalMinor.Should().Be(30000, "the refund fee comes back on; the refund counts on its own");
        orders[1].RefundedMinor.Should().Be(5000);
        pretix.Requests.Count(r => string.Equals(r.Route, $"GET {Api}orders/", StringComparison.Ordinal)).Should().Be(2);
    }

    [HumansFact]
    public async Task ListOrders_ChargesWhatWasReceivedAndCountsExternalRefundsAndChargebacks()
    {
        var pretix = new FakePretix()
            .On("GET", $"{Api}items/", HttpStatusCode.OK, new { results = new object[] { new { id = 7, internal_name = "humans-ticket" } } })
            .On("GET", $"{Api}orders/", HttpStatusCode.OK, new
            {
                results = new object[]
                {
                    new
                    {
                        code = Code,
                        status = "p",
                        url = $"https://pretix.test/nobodies/nowhere2027/order/{Code}/secret/",
                        total = "300.00",
                        datetime = "2027-01-05T09:30:00+00:00",
                        positions = new object[] { new { id = 1, positionid = 1, item = 7, price = "300.00", tax_rate = "10.00", canceled = false } },
                        fees = Array.Empty<object>(),
                        payments = new object[]
                        {
                            new { local_id = 1, state = "confirmed", amount = "300.00", provider = "stripe", payment_date = "2027-01-05T09:31:00+00:00" },
                            new { local_id = 2, state = "confirmed", amount = "300.00", provider = "stripe", payment_date = "2027-01-05T09:32:00+00:00" },
                        },
                        refunds = new object[]
                        {
                            new { local_id = 1, state = "external", source = "external", amount = "40.00", payment = 1, comment = (string?)null, provider = "stripe", details = new { id = "re_1" } },
                            new { local_id = 2, state = "external", source = "external", amount = "60.00", payment = 2, comment = (string?)null, provider = "stripe", details = new { id = "du_1" } },
                        },
                        api_meta = new { humans_order_id = OrderId.ToString() },
                    },
                },
            });

        var order = (await Create(pretix).ListOrdersAsync(
            "nowhere2027", Instant.FromUtc(2027, 1, 1, 0, 0), Instant.FromUtc(2027, 2, 1, 0, 0), TestContext.Current.CancellationToken)).Single();

        order.TotalMinor.Should().Be(60000, "paid twice");
        order.RefundedMinor.Should().Be(4000, "a refund made outside pretix has left");
        order.DisputedMinor.Should().Be(6000, "a chargeback is not a refund");
    }

    [HumansFact]
    public async Task ListOrders_WithoutAnEventSlug_Throws()
    {
        var act = () => Create(new FakePretix()).ListOrdersAsync(
            null, Instant.FromUtc(2027, 1, 1, 0, 0), Instant.FromUtc(2027, 2, 1, 0, 0), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<TicketCheckoutException>();
    }
}
