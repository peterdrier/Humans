using System.Net;
using AwesomeAssertions;
using Humans.BoxOffice.Services;
using Humans.Testing;
using Humans.Tickets.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Stripe;
using Xunit;

namespace Humans.BoxOffice.Tests;

public sealed class BoxOfficeCheckoutBackendTests
{
    private static readonly Instant Now = Instant.FromUtc(2027, 1, 10, 12, 0);
    private readonly IHttpClient _http = Substitute.For<IHttpClient>();
    private readonly List<StripeRequest> _requests = [];

    private BoxOfficeCheckoutBackend Backend() =>
        new(Options.Create(new BoxOfficeSettings { SecretKey = "sk_test_boxoffice" }), new FakeClock(Now),
            NullLogger<BoxOfficeCheckoutBackend>.Instance, _http);

    private void Respond(params string[] bodies)
    {
        using var headers = new HttpResponseMessage();
        var responses = bodies.Select(b => new StripeResponse(HttpStatusCode.OK, headers.Headers, b)).ToArray();
        _http.MakeRequestAsync(Arg.Do<StripeRequest>(_requests.Add), Arg.Any<CancellationToken>())
            .Returns(responses[0], responses.Skip(1).ToArray());
    }

    private static string Session(string status, string paymentStatus, string charge = "null") => $$$"""
        {"id":"cs_test_1","object":"checkout.session","status":"{{{status}}}","payment_status":"{{{paymentStatus}}}",
         "url":"https://checkout.stripe.com/c/pay/cs_test_1","client_reference_id":"order-1",
         "payment_intent":{"id":"pi_1","object":"payment_intent","status":"succeeded","latest_charge":{{{charge}}}}}
        """;

    private const string PaidCharge = """
        {"id":"ch_1","object":"charge","paid":true,"amount":25000,"amount_refunded":5000,"created":1799582400,
         "payment_method_details":{"type":"ideal"},
         "balance_transaction":{"id":"txn_1","object":"balance_transaction","fee":29,"currency":"eur"}}
        """;

    [HumansFact]
    public void Unconfigured_backend_says_so()
    {
        new BoxOfficeCheckoutBackend(Options.Create(new BoxOfficeSettings()), new FakeClock(Now),
            NullLogger<BoxOfficeCheckoutBackend>.Instance).IsConfigured.Should().BeFalse();
    }

    [HumansFact]
    public async Task Checkout_charges_each_line_exactly_and_is_idempotent_per_order()
    {
        Respond(Session("open", "unpaid"));
        var orderId = Guid.NewGuid();

        var session = await Backend().CreateCheckoutAsync(new CheckoutRequest(
            orderId, Guid.NewGuid(), null, "Ana García", "ana@example.com", "EUR", "ca-ES",
            [
                new CheckoutLine(CheckoutLineKind.Ticket, "Ticket: Wave 1", 22500, 0.10m, "Ana García"),
                new CheckoutLine(CheckoutLineKind.Donation, "Donation", 77500, 0m, null),
            ],
            Now + Duration.FromHours(1), new Uri("https://humans.example/Tickets/Buy/Order/x")), TestContext.Current.CancellationToken);

        session.BackendOrderId.Should().Be("cs_test_1");
        session.PaymentUrl.Should().Be(new Uri("https://checkout.stripe.com/c/pay/cs_test_1"));
        var request = _requests.Single();
        request.StripeHeaders["Idempotency-Key"].Should().Be($"humans_ticket_checkout_{orderId}");
        var body = Uri.UnescapeDataString(await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.Should().Contain("line_items[0][price_data][unit_amount]=22500")
            .And.Contain("line_items[1][price_data][unit_amount]=77500")
            .And.Contain("locale=auto")
            .And.Contain($"metadata[humans_ticket_order_id]={orderId}");
    }

    [HumansFact]
    public async Task Non_euro_checkout_is_refused_before_calling_Stripe()
    {
        var act = () => Backend().CreateCheckoutAsync(new CheckoutRequest(
            Guid.NewGuid(), Guid.NewGuid(), null, "A", "a@example.com", "USD", "en", [new CheckoutLine(CheckoutLineKind.Ticket, "T", 100, 0m, "A")],
            Now, new Uri("https://humans.example/")), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<TicketCheckoutException>();
        _requests.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Status_of_a_paid_session_carries_fee_method_and_refunded_total()
    {
        Respond(Session("complete", "paid", PaidCharge));

        var status = await Backend().GetStatusAsync("cs_test_1", TestContext.Current.CancellationToken);

        status.State.Should().Be(CheckoutState.Paid);
        status.FeeMinor.Should().Be(29);
        status.PaymentMethod.Should().Be("ideal");
        status.RefundedMinor.Should().Be(5000);
        status.PaidAt.Should().NotBeNull();
    }

    [HumansTheory]
    [Xunit.InlineData("open", "unpaid", CheckoutState.Pending)]
    [Xunit.InlineData("expired", "unpaid", CheckoutState.Expired)]
    [Xunit.InlineData("complete", "unpaid", CheckoutState.Pending)]
    public async Task Status_maps_open_expired_and_async_in_flight(string status, string paymentStatus, CheckoutState expected)
    {
        Respond(Session(status, paymentStatus));

        (await Backend().GetStatusAsync("cs_test_1", TestContext.Current.CancellationToken)).State.Should().Be(expected);
    }

    [HumansFact]
    public async Task Refund_above_what_the_payment_still_holds_is_refused_without_a_refund_call()
    {
        Respond(Session("complete", "paid", PaidCharge));

        var result = await Backend().RefundAsync(
            new RefundRequest(Guid.NewGuid(), "cs_test_1", 20001, "too much"), TestContext.Current.CancellationToken);

        result.State.Should().Be(RefundState.Failed);
        _requests.Should().ContainSingle();
    }

    [HumansFact]
    public async Task Refund_sends_the_exact_amount_against_the_sessions_payment_with_the_refund_id_as_idempotency_key()
    {
        Respond(Session("complete", "paid", PaidCharge),
            """{"id":"re_1","object":"refund","status":"succeeded","amount":12000,"currency":"eur"}""");
        var refundId = Guid.NewGuid();

        var result = await Backend().RefundAsync(
            new RefundRequest(refundId, "cs_test_1", 12000, "returned"), TestContext.Current.CancellationToken);

        result.State.Should().Be(RefundState.Succeeded);
        result.BackendRefundId.Should().Be("re_1");
        var refund = _requests[1];
        refund.StripeHeaders["Idempotency-Key"].Should().Be($"humans_ticket_refund_{refundId}");
        var body = Uri.UnescapeDataString(await refund.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.Should().Contain("amount=12000").And.Contain("payment_intent=pi_1");
    }
}
