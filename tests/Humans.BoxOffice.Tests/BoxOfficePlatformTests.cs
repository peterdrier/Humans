using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Humans.BoxOffice.Controllers;
using Humans.BoxOffice.Services;
using Humans.Tickets.Contracts;
using Humans.Stripe.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Stripe;
using Xunit;

namespace Humans.BoxOffice.Tests;

public sealed class BoxOfficePlatformTests
{
    [HumansTheory]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData("rk_test_bad suffix")]
    [InlineData("rk_test_secret\r\nhidden")]
    public async Task Platform_UnusableKey_RefusesBeforeSdk(string key)
    {
        using var assertions = new AwesomeAssertions.Execution.AssertionScope();
        var platform = Platform(secretKey: key);
        var ct = TestContext.Current.CancellationToken;
        platform.IsConfigured.Should().BeFalse();
        var findings = await platform.CheckAsync(null, ct);
        findings.Should().ContainSingle().Which.Ok.Should().BeFalse();
        findings.Should().OnlyContain(f => !f.Text.Contains("rk_test_secret", StringComparison.Ordinal));
        Func<Task>[] operations =
        [
            () => platform.CreateOrderAsync(Order(Now + Duration.FromHours(1), new PlatformOrderLine(OrderLineKind.Ticket, "Entry", 1000, 0.21m, "Ana")), ct),
            () => platform.GetStatusAsync("cs_test_config", ct),
            () => platform.ListOrdersAsync(null, Now - Duration.FromDays(1), Now, ct),
            () => platform.RefundAsync(new RefundRequest(Guid.NewGuid(), "cs_test_config", 1000, "Resold"), ct),
            () => platform.CancelAsync("cs_test_config", ct),
        ];
        foreach (var operation in operations)
            await operation.Should().ThrowAsync<TicketingPlatformException>().WithMessage("*not configured*");
        (await platform.GetPaymentLedgerAsync(Now - Duration.FromDays(1), Now, ct)).Should().BeNull();
        _http.ReceivedCalls().Should().BeEmpty();
    }

    private static readonly Instant Now = Instant.FromUtc(2027, 1, 10, 12, 0);
    private readonly IStripeService _stripe = Substitute.For<IStripeService>();
    private readonly IHttpClient _http = Substitute.For<IHttpClient>();
    private readonly List<StripeRequest> _requests = [];

    private const string WebhookSecret = "whsec_test_boxoffice";

    private BoxOfficePlatform Platform(string webhookSecret = "", string secretKey = "sk_test_boxoffice") =>
        new(Options.Create(new BoxOfficeSettings { SecretKey = secretKey, WebhookSecret = webhookSecret }),
            _stripe, new FakeClock(Now), NullLogger<BoxOfficePlatform>.Instance, _http);

    private static PlatformOrderRequest Order(Instant expiresAt, params PlatformOrderLine[] lines) => new(
        Guid.NewGuid(), Guid.NewGuid(), null, "Ana García", "ana@example.com", "EUR", "es-ES", lines, expiresAt,
        new Uri("https://humans.example/Tickets/Buy/Order/x"));

    /// <summary>A real <c>Stripe-Signature</c> header, so the parse runs Stripe.NET's own verification.</summary>
    private static string Sign(string payload)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(WebhookSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{ts}.{payload}"));
        return $"t={ts},v1={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static string Event(string type, string dataObject) =>
        $$$"""{"id":"evt_1","object":"event","type":"{{{type}}}","data":{"object":{{{dataObject}}}}}""";

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
    public void IsConfigured_NoKey_IsFalse()
    {
        new BoxOfficePlatform(Options.Create(new BoxOfficeSettings()), _stripe, new FakeClock(Now),
            NullLogger<BoxOfficePlatform>.Instance).IsConfigured.Should().BeFalse();
    }

    [HumansFact]
    public void Setup_NeverShowsKey()
    {
        var setup = Platform().Setup;

        setup.Hosting.Should().Contain("test mode");
        setup.Settings.Should().OnlyContain(s => s.IsSecret && s.Value == null);
        setup.Settings.Single(s => string.Equals(s.Name, "STRIPE_TICKETS_KEY", StringComparison.Ordinal)).IsSet.Should().BeTrue();
    }

    [HumansFact]
    public async Task Check_Configured_UsesConnectorFindings()
    {
        var ct = TestContext.Current.CancellationToken;
        _stripe.CheckAccountAsync("Tickets", ct, includeCheckout: true).Returns(
            [new(true, "Checkout Sessions (test mode)"), new(false, "Payouts refused (403)")]);

        var findings = await Platform().CheckAsync(null, ct);

        findings.Should().SatisfyRespectively(
            checkout => checkout.Should().Be(new PlatformFinding(true, "Checkout Sessions (test mode)")),
            payouts => payouts.Should().Be(new PlatformFinding(false, "Payouts refused (403)")),
            webhook => webhook.Text.Should().Contain("STRIPE_BOXOFFICE_WEBHOOK_SECRET"));
        await _stripe.Received(1).CheckAccountAsync("Tickets", ct, includeCheckout: true);
        _requests.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Check_CallerCancellation_Propagates()
    {
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();
        _stripe.CheckAccountAsync("Tickets", caller.Token, includeCheckout: true)
            .Returns<Task<IReadOnlyList<StripeAccountCheck>>>(_ => throw new OperationCanceledException(caller.Token));

        var check = () => Platform().CheckAsync(null, caller.Token);

        await check.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansFact]
    public async Task Checkout_ChargesEachLineExactly()
    {
        Respond(Session("open", "unpaid"));
        var orderId = Guid.NewGuid();

        var session = await Platform().CreateOrderAsync(new PlatformOrderRequest(
            orderId, Guid.NewGuid(), null, "Ana García", "ana@example.com", "EUR", "ca-ES",
            [
                new PlatformOrderLine(OrderLineKind.Ticket, "Ticket: Wave 1", 22500, 0.10m, "Ana García"),
                new PlatformOrderLine(OrderLineKind.Donation, "Donation", 77500, 0m, null),
            ],
            Now + Duration.FromHours(1), new Uri("https://humans.example/Tickets/Buy/Order/x")), TestContext.Current.CancellationToken);

        session.PlatformOrderId.Should().Be("cs_test_1");
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
    public async Task List_Period_ReadsEverySession()
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

        var orders = await Platform().ListOrdersAsync(null, Now - Duration.FromDays(30), Now, TestContext.Current.CancellationToken);

        orders.Should().Equal(
            new PlatformOrderSummary("cs_1", orderId, Instant.FromUnixTimeSeconds(1799582400), PlatformOrderState.Paid, 30000, 2, 0, 0),
            new PlatformOrderSummary("cs_2", null, Instant.FromUnixTimeSeconds(1799586000), PlatformOrderState.Paid, 25000, null, 5000, 0));
        _requests.Should().HaveCount(2);
        _requests[0].Method.Should().Be(HttpMethod.Get);
    }

    [HumansFact]
    public async Task Checkout_NonEuroOrder_IsRefused()
    {
        var act = () => Platform().CreateOrderAsync(new PlatformOrderRequest(
            Guid.NewGuid(), Guid.NewGuid(), null, "A", "a@example.com", "USD", "en", [new PlatformOrderLine(OrderLineKind.Ticket, "T", 100, 0m, "A")],
            Now, new Uri("https://humans.example/")), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<TicketingPlatformException>();
        _requests.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Status_PaidSession_CarriesFee()
    {
        Respond(Session("complete", "paid", PaidCharge),
            RefundList(Refund("re_1", "succeeded", 5000), Refund("re_2", "pending", 3000), Refund("re_3", "failed", 2000, Guid.NewGuid())));

        _stripe.GetAccountPaymentFeeAsync("Tickets", "pi_1", TestContext.Current.CancellationToken).Returns(29L);
        var status = await Platform().GetStatusAsync("cs_test_1", TestContext.Current.CancellationToken);

        await _stripe.Received(1).GetAccountPaymentFeeAsync("Tickets", "pi_1", TestContext.Current.CancellationToken);
        status.State.Should().Be(PlatformOrderState.Paid);
        status.FeeMinor.Should().Be(29);
        status.PaymentMethod.Should().Be("ideal");
        status.RefundedMinor.Should().Be(5000);
        status.RefundFailed.Should().BeTrue();
        status.PaidAt.Should().NotBeNull();
    }

    [HumansTheory]
    [InlineData("open", "unpaid", "requires_payment_method", PlatformOrderState.Pending)]
    [InlineData("expired", "unpaid", "canceled", PlatformOrderState.Expired)]
    [InlineData("complete", "unpaid", "processing", PlatformOrderState.Processing)]
    [InlineData("complete", "unpaid", "requires_action", PlatformOrderState.Processing)]
    [InlineData("complete", "unpaid", "requires_payment_method", PlatformOrderState.Failed)]
    [InlineData("complete", "unpaid", "canceled", PlatformOrderState.Failed)]
    public async Task Status_SessionState_MapsToOrderStatus(
        string status, string paymentStatus, string intent, PlatformOrderState expected)
    {
        Respond(Session(status, paymentStatus, intent: intent), RefundList());

        (await Platform().GetStatusAsync("cs_test_1", TestContext.Current.CancellationToken)).State.Should().Be(expected);
    }

    [HumansFact]
    public async Task Status_Chargeback_CountsOpenOrLostDisputes()
    {
        var chargedBack = PaidCharge.Replace("\"paid\":true", "\"paid\":true,\"disputed\":true", StringComparison.Ordinal);
        Respond(Session("complete", "paid", chargedBack), RefundList(),
            """
            {"object":"list","url":"/v1/disputes","has_more":false,"data":[
             {"id":"dp_1","object":"dispute","amount":25000,"status":"lost","charge":"ch_1"},
             {"id":"dp_2","object":"dispute","amount":1000,"status":"won","charge":"ch_1"},
             {"id":"dp_3","object":"dispute","amount":2000,"status":"warning_needs_response","charge":"ch_1"}]}
            """);

        var status = await Platform().GetStatusAsync("cs_test_1", TestContext.Current.CancellationToken);

        status.ChargedBackMinor.Should().Be(25000);
        status.State.Should().Be(PlatformOrderState.Paid);
    }

    [HumansFact]
    public async Task Cancel_BankDebitFailed_Succeeds()
    {
        _http.MakeRequestAsync(Arg.Any<StripeRequest>(), Arg.Any<CancellationToken>()).Returns(
            new StripeResponse(HttpStatusCode.BadRequest, null,
                """{"error":{"type":"invalid_request_error","message":"Only open sessions can be expired."}}"""),
            new StripeResponse(HttpStatusCode.OK, null, Session("complete", "unpaid", intent: "requires_payment_method")),
            new StripeResponse(HttpStatusCode.BadRequest, null,
                """{"error":{"type":"invalid_request_error","message":"Only open sessions can be expired."}}"""),
            new StripeResponse(HttpStatusCode.OK, null, Session("complete", "unpaid", intent: "processing")));

        await Platform().CancelAsync("cs_test_1", TestContext.Current.CancellationToken);
        var settling = () => Platform().CancelAsync("cs_test_1", TestContext.Current.CancellationToken);

        await settling.Should().ThrowAsync<TicketingPlatformException>();
    }

    [HumansFact]
    public async Task Refund_AboveRemainingAmount_IsRefused()
    {
        Respond(Session("complete", "paid", PaidCharge), RefundList());

        var result = await Platform().RefundAsync(
            new RefundRequest(Guid.NewGuid(), "cs_test_1", 20001, "too much"), TestContext.Current.CancellationToken);

        result.State.Should().Be(RefundState.Failed);
        _requests.Should().HaveCount(2).And.OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [HumansFact]
    public async Task Refund_SendsExactAmount()
    {
        Respond(Session("complete", "paid", PaidCharge), RefundList(),
            """{"id":"re_1","object":"refund","status":"succeeded","amount":12000,"currency":"eur"}""");
        var refundId = Guid.NewGuid();

        var result = await Platform().RefundAsync(
            new RefundRequest(refundId, "cs_test_1", 12000, "resale"), TestContext.Current.CancellationToken);

        result.State.Should().Be(RefundState.Succeeded);
        result.PlatformRefundId.Should().Be("re_1");
        var refund = _requests[2];
        refund.StripeHeaders["Idempotency-Key"].Should().Be($"humans_ticket_refund_{refundId}");
        var body = Uri.UnescapeDataString(await refund.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.Should().Contain("amount=12000").And.Contain("payment_intent=pi_1");
    }

    [HumansFact]
    public async Task Refund_Repeated_ReturnsEarlierState()
    {
        var refundId = Guid.NewGuid();
        Respond(Session("complete", "paid", PaidCharge), RefundList(Refund("re_7", "pending", 30000, refundId)));

        var result = await Platform().RefundAsync(
            new RefundRequest(refundId, "cs_test_1", 30000, "resale"), TestContext.Current.CancellationToken);

        result.State.Should().Be(RefundState.Pending);
        result.PlatformRefundId.Should().Be("re_7");
        _requests.Should().HaveCount(2).And.OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [HumansTheory]
    [InlineData(500, "api_error")]
    [InlineData(502, "api_error")]
    [InlineData(503, "api_error")]
    [InlineData(504, "api_error")]
    [InlineData(409, "invalid_request_error")]
    [InlineData(409, "idempotency_error")]
    [InlineData(400, "idempotency_error")]
    [InlineData(400, "api_error")]
    [InlineData(429, "rate_limit_error")]
    public async Task RefundAsync_UnresolvedError_Throws(int status, string type)
    {
        RefundError(status, type);
        var refund = () => Platform().RefundAsync(
            new RefundRequest(Guid.NewGuid(), "cs_test_1", 12000, "Returned"), TestContext.Current.CancellationToken);

        await refund.Should().ThrowAsync<TicketingPlatformException>().WithMessage("*refund-unresolved*");
        _requests.Should().HaveCount(3);
        _requests[^1].Method.Should().Be(HttpMethod.Post);
    }

    [HumansTheory]
    [InlineData(400, "invalid_request_error")]
    [InlineData(401, "authentication_error")]
    [InlineData(402, "card_error")]
    [InlineData(403, "invalid_request_error")]
    [InlineData(404, "invalid_request_error")]
    public async Task RefundAsync_DefinitiveRejection_ReturnsFailed(int status, string type)
    {
        RefundError(status, type);

        var result = await Platform().RefundAsync(
            new RefundRequest(Guid.NewGuid(), "cs_test_1", 12000, "Returned"), TestContext.Current.CancellationToken);

        result.State.Should().Be(RefundState.Failed);
        result.PlatformRefundId.Should().BeNull();
        result.FailureReason.Should().Be("refund-unresolved");
        _requests.Should().HaveCount(3);
    }

    private void RefundError(int status, string type)
    {
        using var headers = new HttpResponseMessage();
        var session = new StripeResponse(HttpStatusCode.OK, headers.Headers, Session("complete", "paid", PaidCharge));
        var refunds = new StripeResponse(HttpStatusCode.OK, headers.Headers, RefundList());
        var error = new StripeResponse((HttpStatusCode)status, headers.Headers,
            $$$"""{"error":{"type":"{{{type}}}","message":"refund-unresolved"}}""");
        _http.MakeRequestAsync(Arg.Do<StripeRequest>(_requests.Add), Arg.Any<CancellationToken>())
            .Returns(session, refunds, error);
    }

    [HumansFact]
    public async Task Webhook_BadSignature_IsRejected()
    {
        var platform = new BoxOfficePlatform(
            Options.Create(new BoxOfficeSettings { SecretKey = "sk_test_boxoffice", WebhookSecret = "whsec_test" }), _stripe, new FakeClock(Now),
            NullLogger<BoxOfficePlatform>.Instance, _http);

        (await platform.ParseWebhookAsync("{}", "t=1,v1=bad", TestContext.Current.CancellationToken)).Should().BeNull();
        _requests.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Ledger_EachMovement_NamesItsOrder()
    {
        var paidHere = Guid.NewGuid();
        var refundedLater = Guid.NewGuid();
        var from = Now - Duration.FromDays(1);
        var ct = TestContext.Current.CancellationToken;
        StripeLedgerPayment Payment(Guid orderId) => new("pi_" + orderId, null,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["humans_ticket_order_id"] = orderId.ToString() });
        _stripe.GetAccountLedgerAsync("Tickets", from, Now, ct).Returns(new StripeAccountLedger(
            [
                new("txn_c", Now, "charge", 30000, 450, null, Payment(paidHere)),
                new("txn_r", Now, "refund", -5000, 0, null, Payment(refundedLater)),
                new("txn_f", Now, "fee", -200, 0, "Billing", null),
                new("txn_p", Now, "payout", -24350, 0, null, null),
            ],
            [
                new("po_1", Now, Now, 24350, "paid", 24350, 3),
                new("po_2", Now, Now, 1000, "pending", null, 0),
                new("po_3", Now, Now, 700, "paid", null, 0),
            ]));

        var ledger = await Platform().GetPaymentLedgerAsync(Now - Duration.FromDays(1), Now, TestContext.Current.CancellationToken);

        ledger!.Entries.Select(e => (e.Id, e.Kind, e.AmountMinor, e.FeeMinor, e.HumansOrderId)).Should().Equal(
            ("txn_c", PaymentLedgerEntryKind.Charge, 30000L, 450L, paidHere),
            ("txn_r", PaymentLedgerEntryKind.Refund, -5000L, 0L, refundedLater),
            ("txn_f", PaymentLedgerEntryKind.Fee, -200L, 0L, null),
            ("txn_p", PaymentLedgerEntryKind.Payout, -24350L, 0L, null));
        ledger.Payouts.Select(p => (p.Id, p.AmountMinor, p.Status, p.ContentsNetMinor, p.ContentsCount)).Should().Equal(
            ("po_1", 24350L, "paid", 24350, 3), ("po_2", 1000L, "pending", null, 0),
            ("po_3", 700L, "paid", null, 0));
        await _stripe.Received(1).GetAccountLedgerAsync("Tickets", from, Now, ct);
        _requests.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Ledger_UnconfiguredReporting_ReturnsUnknown()
    {
        _stripe.GetAccountLedgerAsync("Tickets", Arg.Any<Instant>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns((StripeAccountLedger?)null);

        (await Platform().GetPaymentLedgerAsync(Now, Now, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [HumansFact]
    public async Task Ledger_ReportingRefusal_ThrowsPlatformException()
    {
        _stripe.GetAccountLedgerAsync("Tickets", Arg.Any<Instant>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns<Task<StripeAccountLedger?>>(_ => throw new InvalidOperationException("Stripe refused the Tickets account"));

        var read = () => Platform().GetPaymentLedgerAsync(Now, Now, TestContext.Current.CancellationToken);

        await read.Should().ThrowAsync<TicketingPlatformException>().WithMessage("*Tickets account*");
    }

    [HumansFact]
    public async Task Checkout_NegativeLineOrZeroTotal_IsRefused()
    {
        var negative = () => Platform().CreateOrderAsync(Order(Now + Duration.FromHours(1),
            new PlatformOrderLine(OrderLineKind.Ticket, "T", 500, 0m, "A"),
            new PlatformOrderLine(OrderLineKind.Donation, "D", -100, 0m, null)), TestContext.Current.CancellationToken);
        var nothing = () => Platform().CreateOrderAsync(Order(Now + Duration.FromHours(1),
            new PlatformOrderLine(OrderLineKind.Ticket, "T", 0, 0m, "A")), TestContext.Current.CancellationToken);

        await negative.Should().ThrowAsync<TicketingPlatformException>();
        await nothing.Should().ThrowAsync<TicketingPlatformException>();
        _requests.Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData(5, 31)]
    [InlineData(72 * 60, 23 * 60)]
    public async Task Checkout_FreeLines_AreLeftOut(int askedMinutes, int sentMinutes)
    {
        Respond(Session("open", "unpaid"));

        await Platform().CreateOrderAsync(Order(Now + Duration.FromMinutes(askedMinutes),
            new PlatformOrderLine(OrderLineKind.Ticket, "Companion", 0, 0m, "Bo"),
            new PlatformOrderLine(OrderLineKind.Ticket, "Ticket", 22500, 0.10m, "Ana García")), TestContext.Current.CancellationToken);

        var body = Uri.UnescapeDataString(await _requests.Single().Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.Should().Contain($"expires_at={(Now + Duration.FromMinutes(sentMinutes)).ToUnixTimeSeconds()}")
            .And.Contain("line_items[0][price_data][unit_amount]=22500")
            .And.NotContain("line_items[1]")
            .And.Contain("metadata[humans_ticket_count]=2");
    }

    [HumansFact]
    public async Task Status_ForeignFailedRefund_DoesNotFlagOrder()
    {
        Respond(Session("complete", "paid", PaidCharge), RefundList(Refund("re_1", "failed", 2000)));

        var status = await Platform().GetStatusAsync("cs_test_1", TestContext.Current.CancellationToken);

        status.RefundFailed.Should().BeFalse();
        status.RefundedMinor.Should().Be(0);
    }

    [HumansFact]
    public async Task Refund_NoSettledPayment_Fails()
    {
        Respond(Session("complete", "unpaid", PaidCharge.Replace("\"paid\":true", "\"paid\":false", StringComparison.Ordinal)));

        var result = await Platform().RefundAsync(
            new RefundRequest(Guid.NewGuid(), "cs_test_1", 1000, "resale"), TestContext.Current.CancellationToken);

        result.State.Should().Be(RefundState.Failed);
        _requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Get);
    }

    [HumansFact]
    public async Task Refund_StripeDeclines_FailsWithStripesReason()
    {
        using var headers = new HttpResponseMessage();
        _http.MakeRequestAsync(Arg.Any<StripeRequest>(), Arg.Any<CancellationToken>()).Returns(
            new StripeResponse(HttpStatusCode.OK, headers.Headers, Session("complete", "paid", PaidCharge)),
            new StripeResponse(HttpStatusCode.OK, headers.Headers, RefundList()),
            new StripeResponse(HttpStatusCode.BadRequest, headers.Headers,
                """{"error":{"type":"invalid_request_error","message":"Charge ch_1 has been disputed."}}"""));

        var result = await Platform().RefundAsync(
            new RefundRequest(Guid.NewGuid(), "cs_test_1", 1000, "resale"), TestContext.Current.CancellationToken);

        result.State.Should().Be(RefundState.Failed);
        result.FailureReason.Should().Be("Charge ch_1 has been disputed.");
    }

    [HumansFact]
    public async Task Refund_StripeNeverAnswers_Throws()
    {
        using var headers = new HttpResponseMessage();
        _http.MakeRequestAsync(Arg.Any<StripeRequest>(), Arg.Any<CancellationToken>()).Returns(
            _ => new StripeResponse(HttpStatusCode.OK, headers.Headers, Session("complete", "paid", PaidCharge)),
            _ => new StripeResponse(HttpStatusCode.OK, headers.Headers, RefundList()),
            _ => throw new StripeException("no answer"));

        var act = () => Platform().RefundAsync(
            new RefundRequest(Guid.NewGuid(), "cs_test_1", 1000, "resale"), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<TicketingPlatformException>();
    }

    [HumansFact]
    public async Task Cancel_PaidSession_IsRefused()
    {
        _http.MakeRequestAsync(Arg.Any<StripeRequest>(), Arg.Any<CancellationToken>()).Returns(
            new StripeResponse(HttpStatusCode.BadRequest, null,
                """{"error":{"type":"invalid_request_error","message":"Only open sessions can be expired."}}"""),
            new StripeResponse(HttpStatusCode.OK, null, Session("complete", "paid", PaidCharge)));

        var act = () => Platform().CancelAsync("cs_test_1", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<TicketingPlatformException>();
    }

    [HumansFact]
    public async Task Webhook_SessionEvent_NamesItsSession()
    {
        var body = Event("checkout.session.completed", """{"id":"cs_9","object":"checkout.session"}""");

        (await Platform(WebhookSecret).ParseWebhookAsync(body, Sign(body), TestContext.Current.CancellationToken))
            .Should().Equal("cs_9");
        _requests.Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData("charge.refunded", """{"id":"ch_1","object":"charge","payment_intent":"pi_1"}""")]
    [InlineData("refund.failed", """{"id":"re_1","object":"refund","payment_intent":"pi_1"}""")]
    [InlineData("charge.dispute.created", """{"id":"dp_1","object":"dispute","payment_intent":"pi_1"}""")]
    public async Task Webhook_MoneyEvent_NamesPaymentSessions(string type, string dataObject)
    {
        Respond("""{"object":"list","url":"/v1/checkout/sessions","has_more":false,"data":[{"id":"cs_1","object":"checkout.session"}]}""");
        var body = Event(type, dataObject);

        (await Platform(WebhookSecret).ParseWebhookAsync(body, Sign(body), TestContext.Current.CancellationToken))
            .Should().Equal("cs_1");
        _requests.Single().Uri.Query.Should().Contain("payment_intent=pi_1");
    }

    [HumansFact]
    public async Task Webhook_UnrelatedEvent_NamesNothing()
    {
        var body = Event("customer.created", """{"id":"cus_1","object":"customer"}""");

        (await Platform(WebhookSecret).ParseWebhookAsync(body, Sign(body), TestContext.Current.CancellationToken))
            .Should().BeEmpty();
    }

    private static BoxOfficeStripeWebhookController Webhook(
        BoxOfficePlatform platform, ITicketingPlatformEvents events, string body, string signature)
    {
        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        http.Request.Headers["Stripe-Signature"] = signature;
        return new BoxOfficeStripeWebhookController(platform, events, NullLogger<BoxOfficeStripeWebhookController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    [HumansFact]
    public async Task Webhook_Verified_NudgesTickets()
    {
        var events = Substitute.For<ITicketingPlatformEvents>();
        var body = Event("checkout.session.expired", """{"id":"cs_9","object":"checkout.session"}""");
        var ct = TestContext.Current.CancellationToken;

        var unset = await Webhook(Platform(), events, body, Sign(body)).Receive(ct);
        var forged = await Webhook(Platform(WebhookSecret), events, body, "t=1,v1=bad").Receive(ct);
        await events.DidNotReceiveWithAnyArgs().PlatformOrderChangedAsync(default!, default!);
        var verified = await Webhook(Platform(WebhookSecret), events, body, Sign(body)).Receive(ct);

        unset.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        forged.Should().BeOfType<BadRequestResult>();
        verified.Should().BeOfType<OkResult>();
        await events.Received(1).PlatformOrderChangedAsync("boxoffice", "cs_9", Arg.Any<CancellationToken>());
    }
}
