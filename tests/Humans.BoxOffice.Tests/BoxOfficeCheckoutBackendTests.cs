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

    private static string Session(string status, string paymentStatus, string charge = "null", string intent = "succeeded") => $$$"""
        {"id":"cs_test_1","object":"checkout.session","status":"{{{status}}}","payment_status":"{{{paymentStatus}}}",
         "url":"https://checkout.stripe.com/c/pay/cs_test_1","client_reference_id":"order-1",
         "payment_intent":{"id":"pi_1","object":"payment_intent","status":"{{{intent}}}","latest_charge":{{{charge}}}}}
        """;

    private static string RefundList(params string[] refunds) =>
        $$"""{"object":"list","url":"/v1/refunds","has_more":false,"data":[{{string.Join(",", refunds)}}]}""";

    private static string Refund(string id, string status, long amount, Guid? refundId = null) => $$$"""
        {"id":"{{{id}}}","object":"refund","status":"{{{status}}}","amount":{{{amount}}},"currency":"eur",
         "payment_intent":"pi_1","metadata":{{{(refundId is { } rid ? $"{{\"humans_ticket_refund_id\":\"{rid}\"}}" : "{}")}}}}
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
    public void Setup_says_test_mode_and_never_shows_the_key()
    {
        var setup = Backend().Setup;

        setup.Hosting.Should().Contain("test mode");
        setup.Settings.Should().OnlyContain(s => s.IsSecret && s.Value == null);
        setup.Settings.Single(s => string.Equals(s.Name, "STRIPE_BOXOFFICE_KEY", StringComparison.Ordinal)).IsSet.Should().BeTrue();
    }

    [HumansFact]
    public async Task Check_reads_sessions_and_flags_a_missing_webhook_secret()
    {
        Respond("""{"object":"list","url":"/v1/checkout/sessions","has_more":false,"data":[]}""");

        var findings = await Backend().CheckAsync(null, TestContext.Current.CancellationToken);

        findings.Should().Contain(f => f.Ok && f.Text.Contains("test mode"));
        findings.Should().ContainSingle(f => !f.Ok).Which.Text.Should().Contain("STRIPE_BOXOFFICE_WEBHOOK_SECRET");
        _requests.Single().Method.Should().Be(HttpMethod.Get);
    }

    [HumansFact]
    public async Task Check_reports_an_unreachable_Stripe_instead_of_throwing()
    {
        _http.MakeRequestAsync(Arg.Any<StripeRequest>(), Arg.Any<CancellationToken>())
            .Returns<StripeResponse>(_ => throw new HttpRequestException("no route"));

        var findings = await Backend().CheckAsync(null, TestContext.Current.CancellationToken);

        findings.Should().Contain(f => !f.Ok && f.Text.Contains("could not be reached"));
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
            .And.Contain($"metadata[humans_ticket_order_id]={orderId}")
            .And.Contain("metadata[humans_ticket_count]=1");
    }

    [HumansFact]
    public async Task List_reads_every_session_of_the_period_and_refunds_only_where_the_charge_shows_some()
    {
        var orderId = Guid.NewGuid();
        Respond(
            """
            {"object":"list","url":"/v1/checkout/sessions","has_more":false,"data":[
             {"id":"cs_1","object":"checkout.session","status":"complete","payment_status":"paid","amount_total":30000,"created":1799582400,
              "metadata":{"humans_ticket_order_id":"ORDER_ID","humans_ticket_count":"2"},"payment_intent":{"id":"pi_1","object":"payment_intent","status":"succeeded",
              "latest_charge":{"id":"ch_1","object":"charge","paid":true,"amount":30000,"amount_refunded":0,"created":1799582400}}},
             {"id":"cs_2","object":"checkout.session","status":"complete","payment_status":"paid","amount_total":25000,"created":1799586000,
              "metadata":{},"payment_intent":{"id":"pi_2","object":"payment_intent","status":"succeeded",
              "latest_charge":{"id":"ch_2","object":"charge","paid":true,"amount":25000,"amount_refunded":5000,"created":1799586000}}}]}
            """.Replace("ORDER_ID", orderId.ToString(), StringComparison.Ordinal),
            RefundList(Refund("re_1", "succeeded", 5000)));

        var orders = await Backend().ListOrdersAsync(null, Now - Duration.FromDays(30), Now, TestContext.Current.CancellationToken);

        orders.Should().Equal(
            new BackendOrderSummary("cs_1", orderId, Instant.FromUnixTimeSeconds(1799582400), CheckoutState.Paid, 30000, 2, 0, 0),
            new BackendOrderSummary("cs_2", null, Instant.FromUnixTimeSeconds(1799586000), CheckoutState.Paid, 25000, null, 5000, 0));
        _requests.Should().HaveCount(2);
        _requests[0].Method.Should().Be(HttpMethod.Get);
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
        Respond(Session("complete", "paid", PaidCharge),
            RefundList(Refund("re_1", "succeeded", 5000), Refund("re_2", "pending", 3000), Refund("re_3", "failed", 2000, Guid.NewGuid())));

        var status = await Backend().GetStatusAsync("cs_test_1", TestContext.Current.CancellationToken);

        status.State.Should().Be(CheckoutState.Paid);
        status.FeeMinor.Should().Be(29);
        status.PaymentMethod.Should().Be("ideal");
        status.RefundedMinor.Should().Be(5000);
        status.RefundFailed.Should().BeTrue();
        status.PaidAt.Should().NotBeNull();
    }

    [HumansTheory]
    [Xunit.InlineData("open", "unpaid", "requires_payment_method", CheckoutState.Pending)]
    [Xunit.InlineData("expired", "unpaid", "canceled", CheckoutState.Expired)]
    [Xunit.InlineData("complete", "unpaid", "processing", CheckoutState.Processing)]
    [Xunit.InlineData("complete", "unpaid", "requires_action", CheckoutState.Processing)]
    [Xunit.InlineData("complete", "unpaid", "requires_payment_method", CheckoutState.Failed)]
    [Xunit.InlineData("complete", "unpaid", "canceled", CheckoutState.Failed)]
    public async Task Status_maps_open_expired_settling_and_failed_debits(
        string status, string paymentStatus, string intent, CheckoutState expected)
    {
        Respond(Session(status, paymentStatus, intent: intent), RefundList());

        (await Backend().GetStatusAsync("cs_test_1", TestContext.Current.CancellationToken)).State.Should().Be(expected);
    }

    [HumansFact]
    public async Task A_disputed_charge_reports_what_open_and_lost_disputes_hold_but_not_inquiries_or_won_ones()
    {
        var disputed = PaidCharge.Replace("\"paid\":true", "\"paid\":true,\"disputed\":true", StringComparison.Ordinal);
        Respond(Session("complete", "paid", disputed), RefundList(),
            """
            {"object":"list","url":"/v1/disputes","has_more":false,"data":[
             {"id":"dp_1","object":"dispute","amount":25000,"status":"lost","charge":"ch_1"},
             {"id":"dp_2","object":"dispute","amount":1000,"status":"won","charge":"ch_1"},
             {"id":"dp_3","object":"dispute","amount":2000,"status":"warning_needs_response","charge":"ch_1"}]}
            """);

        var status = await Backend().GetStatusAsync("cs_test_1", TestContext.Current.CancellationToken);

        status.DisputedMinor.Should().Be(25000);
        status.State.Should().Be(CheckoutState.Paid);
    }

    [HumansFact]
    public async Task Cancelling_a_session_whose_bank_debit_failed_succeeds_but_a_settling_one_is_refused()
    {
        _http.MakeRequestAsync(Arg.Any<StripeRequest>(), Arg.Any<CancellationToken>()).Returns(
            new StripeResponse(HttpStatusCode.BadRequest, null,
                """{"error":{"type":"invalid_request_error","message":"Only open sessions can be expired."}}"""),
            new StripeResponse(HttpStatusCode.OK, null, Session("complete", "unpaid", intent: "requires_payment_method")),
            new StripeResponse(HttpStatusCode.BadRequest, null,
                """{"error":{"type":"invalid_request_error","message":"Only open sessions can be expired."}}"""),
            new StripeResponse(HttpStatusCode.OK, null, Session("complete", "unpaid", intent: "processing")));

        await Backend().CancelAsync("cs_test_1", TestContext.Current.CancellationToken);
        var settling = () => Backend().CancelAsync("cs_test_1", TestContext.Current.CancellationToken);

        await settling.Should().ThrowAsync<TicketCheckoutException>();
    }

    [HumansFact]
    public async Task Refund_above_what_the_payment_still_holds_is_refused_without_a_refund_call()
    {
        Respond(Session("complete", "paid", PaidCharge), RefundList());

        var result = await Backend().RefundAsync(
            new RefundRequest(Guid.NewGuid(), "cs_test_1", 20001, "too much"), TestContext.Current.CancellationToken);

        result.State.Should().Be(RefundState.Failed);
        _requests.Should().HaveCount(2).And.OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [HumansFact]
    public async Task Refund_sends_the_exact_amount_against_the_sessions_payment_with_the_refund_id_as_idempotency_key()
    {
        Respond(Session("complete", "paid", PaidCharge), RefundList(),
            """{"id":"re_1","object":"refund","status":"succeeded","amount":12000,"currency":"eur"}""");
        var refundId = Guid.NewGuid();

        var result = await Backend().RefundAsync(
            new RefundRequest(refundId, "cs_test_1", 12000, "returned"), TestContext.Current.CancellationToken);

        result.State.Should().Be(RefundState.Succeeded);
        result.BackendRefundId.Should().Be("re_1");
        var refund = _requests[2];
        refund.StripeHeaders["Idempotency-Key"].Should().Be($"humans_ticket_refund_{refundId}");
        var body = Uri.UnescapeDataString(await refund.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.Should().Contain("amount=12000").And.Contain("payment_intent=pi_1");
    }

    [HumansFact]
    public async Task A_repeated_refund_gets_the_earlier_refunds_state_without_a_second_refund()
    {
        var refundId = Guid.NewGuid();
        Respond(Session("complete", "paid", PaidCharge), RefundList(Refund("re_7", "pending", 30000, refundId)));

        var result = await Backend().RefundAsync(
            new RefundRequest(refundId, "cs_test_1", 30000, "returned"), TestContext.Current.CancellationToken);

        result.State.Should().Be(RefundState.Pending);
        result.BackendRefundId.Should().Be("re_7");
        _requests.Should().HaveCount(2).And.OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [HumansFact]
    public async Task A_webhook_with_a_bad_signature_is_rejected()
    {
        var backend = new BoxOfficeCheckoutBackend(
            Options.Create(new BoxOfficeSettings { SecretKey = "sk_test_boxoffice", WebhookSecret = "whsec_test" }), new FakeClock(Now),
            NullLogger<BoxOfficeCheckoutBackend>.Instance, _http);

        (await backend.ParseWebhookAsync("{}", "t=1,v1=bad", TestContext.Current.CancellationToken)).Should().BeNull();
        _requests.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Payment_ledger_ties_each_movement_to_its_order_and_lists_payout_contents()
    {
        var paidHere = Guid.NewGuid();
        var refundedLater = Guid.NewGuid();
        Respond(
            """
            {"object":"list","url":"/v1/balance_transactions","has_more":false,"data":[
             {"id":"txn_c","object":"balance_transaction","type":"charge","reporting_category":"charge","amount":30000,"fee":450,"net":29550,"created":1799582400,
              "source":{"id":"ch_1","object":"charge","payment_intent":"pi_new","metadata":{}}},
             {"id":"txn_r","object":"balance_transaction","type":"refund","reporting_category":"refund","amount":-5000,"fee":0,"net":-5000,"created":1799582500,
              "source":{"id":"re_1","object":"refund","payment_intent":"pi_old","metadata":{}}},
             {"id":"txn_f","object":"balance_transaction","type":"stripe_fee","reporting_category":"fee","amount":-200,"fee":0,"net":-200,"created":1799582600,
              "description":"Billing","source":null},
             {"id":"txn_p","object":"balance_transaction","type":"payout","reporting_category":"payout","amount":-24350,"fee":0,"net":-24350,"created":1799582700,
              "source":{"id":"po_1","object":"payout"}}]}
            """,
            $$$"""
            {"object":"list","url":"/v1/payment_intents","has_more":false,"data":[
             {"id":"pi_new","object":"payment_intent","metadata":{"humans_ticket_order_id":"{{{paidHere}}}"}}]}
            """,
            $$$"""{"id":"pi_old","object":"payment_intent","metadata":{"humans_ticket_order_id":"{{{refundedLater}}}"}}""",
            """
            {"object":"list","url":"/v1/payouts","has_more":false,"data":[
             {"id":"po_1","object":"payout","amount":24350,"arrival_date":1799625600,"automatic":true,"status":"paid","created":1799582700},
             {"id":"po_2","object":"payout","amount":1000,"arrival_date":1799625600,"automatic":false,"status":"pending","created":1799582800}]}
            """,
            """
            {"object":"list","url":"/v1/balance_transactions","has_more":false,"data":[
             {"id":"txn_c","object":"balance_transaction","type":"charge","net":29550},
             {"id":"txn_r","object":"balance_transaction","type":"refund","net":-5000},
             {"id":"txn_f","object":"balance_transaction","type":"stripe_fee","net":-200},
             {"id":"txn_p","object":"balance_transaction","type":"payout","net":-24350}]}
            """);

        var ledger = await Backend().GetPaymentLedgerAsync(Now - Duration.FromDays(1), Now, TestContext.Current.CancellationToken);

        ledger!.Entries.Select(e => (e.Id, e.Kind, e.AmountMinor, e.FeeMinor, e.HumansOrderId)).Should().Equal(
            ("txn_c", PaymentLedgerEntryKind.Charge, 30000L, 450L, (Guid?)paidHere),
            ("txn_r", PaymentLedgerEntryKind.Refund, -5000L, 0L, (Guid?)refundedLater),
            ("txn_f", PaymentLedgerEntryKind.Fee, -200L, 0L, (Guid?)null),
            ("txn_p", PaymentLedgerEntryKind.Payout, -24350L, 0L, (Guid?)null));
        ledger.Payouts.Select(p => (p.Id, p.AmountMinor, p.Status, p.ContentsNetMinor, p.ContentsCount)).Should().Equal(
            ("po_1", 24350L, "paid", (long?)24350, 3), ("po_2", 1000L, "pending", (long?)null, 0));
        _requests.Should().HaveCount(5).And.OnlyContain(r => r.Method == HttpMethod.Get);
    }
}
