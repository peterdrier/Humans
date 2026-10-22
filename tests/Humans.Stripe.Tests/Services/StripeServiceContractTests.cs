using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Humans.Stripe.Contracts;
using Humans.Stripe.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using NSubstitute;
// Compilation-unit level, so this binds to the SDK's global::Stripe rather than to the
// enclosing Humans.Stripe section namespace. See StripeService.cs.
using Stripe;

namespace Humans.Stripe.Tests.Services;

/// <summary>
/// A read returns <c>null</c> for the cases the section checks and propagates everything else; the
/// write throws. A read that throws where its siblings return null is the defect these tests catch.
/// </summary>
public class StripeServiceContractTests
{
    private const string TestWebhookSecret = "whsec_test_humans_section_doctor_secret";

    private static (StripeService Service, CapturingLogger<StripeService> Log) Build(
        Action<StripeSettings>? configure = null)
    {
        var settings = new StripeSettings();
        configure?.Invoke(settings);
        var log = new CapturingLogger<StripeService>();
        return (new StripeService(Options.Create(settings), log), log);
    }

    [HumansFact]
    public async Task GetPaymentDetails_returns_null_when_tickets_key_unset()
    {
        var (svc, log) = Build();

        var result = await svc.GetPaymentDetailsAsync("pi_test_x");

        result.Should().BeNull();
        log.Entries.Should().Contain(e => e.Level == LogLevel.Warning);
    }

    [HumansFact]
    public async Task PaymentDetails_waits_for_async_capture_fees_instead_of_recording_zero()
    {
        var http = Substitute.For<IHttpClient>();
        using var headers = new HttpResponseMessage();
        const string pending = """
            {"id":"pi_async","object":"payment_intent","latest_charge":{
              "id":"ch_async","object":"charge","payment_method_details":{"type":"card","card":{"brand":"visa"}},
              "balance_transaction":null}}
            """;
        const string captured = """
            {"id":"pi_async","object":"payment_intent","latest_charge":{
              "id":"ch_async","object":"charge","payment_method_details":{"type":"card","card":{"brand":"visa"}},
              "balance_transaction":{"id":"txn_async","object":"balance_transaction","currency":"eur",
                "fee_details":[{"type":"stripe_fee","amount":123,"currency":"eur"},{"type":"application_fee","amount":45,"currency":"eur"}]}}}
            """;
        http.MakeRequestAsync(Arg.Any<StripeRequest>(), Arg.Any<CancellationToken>()).Returns(
            new StripeResponse(HttpStatusCode.OK, headers.Headers, pending),
            new StripeResponse(HttpStatusCode.OK, headers.Headers, captured));
        var service = new StripeService(Options.Create(new StripeSettings { TicketsKey = "sk_test_fees" }),
            new CapturingLogger<StripeService>(), http);

        (await service.GetPaymentDetailsAsync("pi_async", TestContext.Current.CancellationToken)).Should().BeNull();
        var available = await service.GetPaymentDetailsAsync("pi_async", TestContext.Current.CancellationToken);

        available.Should().NotBeNull();
        available.StripeFee.Should().Be(1.23m);
        available.ApplicationFee.Should().Be(0.45m);
        available.PaymentMethod.Should().Be("card");
        available.PaymentMethodDetail.Should().Be("visa");
    }

    [HumansFact]
    public async Task PaymentDetails_preserves_explicit_zero_fees_on_an_available_balance_transaction()
    {
        var http = Substitute.For<IHttpClient>();
        using var headers = new HttpResponseMessage();
        const string payload = """
            {"id":"pi_zero","object":"payment_intent","latest_charge":{
              "id":"ch_zero","object":"charge","payment_method_details":{"type":"card"},
              "balance_transaction":{"id":"txn_zero","object":"balance_transaction","currency":"eur","fee_details":[]}}}
            """;
        http.MakeRequestAsync(Arg.Any<StripeRequest>(), Arg.Any<CancellationToken>())
            .Returns(new StripeResponse(HttpStatusCode.OK, headers.Headers, payload));
        var service = new StripeService(Options.Create(new StripeSettings { TicketsKey = "sk_test_fees" }),
            new CapturingLogger<StripeService>(), http);

        var available = await service.GetPaymentDetailsAsync("pi_zero", TestContext.Current.CancellationToken);

        available.Should().NotBeNull();
        available.StripeFee.Should().Be(0m);
        available.ApplicationFee.Should().Be(0m);
    }

    [HumansFact]
    public async Task PaymentDetails_captures_every_fee_bucket_and_stays_quiet_when_they_cover_the_total()
    {
        var http = Substitute.For<IHttpClient>();
        using var headers = new HttpResponseMessage();
        const string payload = """
            {"id":"pi_all","object":"payment_intent","latest_charge":{
              "id":"ch_all","object":"charge","payment_method_details":{"type":"card"},
              "balance_transaction":{"id":"txn_all","object":"balance_transaction","currency":"eur","fee":214,
                "fee_details":[{"type":"stripe_fee","amount":100,"currency":"eur"},
                               {"type":"payment_method_passthrough_fee","amount":20,"currency":"eur"},
                               {"type":"application_fee","amount":45,"currency":"eur"},
                               {"type":"tax","amount":25,"currency":"eur"},
                               {"type":"withheld_tax","amount":24,"currency":"eur"}]}}}
            """;
        http.MakeRequestAsync(Arg.Any<StripeRequest>(), Arg.Any<CancellationToken>())
            .Returns(new StripeResponse(HttpStatusCode.OK, headers.Headers, payload));
        var log = new CapturingLogger<StripeService>();
        var service = new StripeService(Options.Create(new StripeSettings { TicketsKey = "sk_test_fees" }), log, http);

        var details = await service.GetPaymentDetailsAsync("pi_all", TestContext.Current.CancellationToken);

        details.Should().NotBeNull();
        details.StripeFee.Should().Be(1.20m);
        details.ApplicationFee.Should().Be(0.45m);
        details.FeeTax.Should().Be(0.25m);
        details.WithheldTax.Should().Be(0.24m);
        log.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
    }

    [HumansFact]
    public async Task PaymentDetails_warns_when_an_unknown_fee_type_leaves_the_total_unaccounted()
    {
        var http = Substitute.For<IHttpClient>();
        using var headers = new HttpResponseMessage();
        const string payload = """
            {"id":"pi_new","object":"payment_intent","latest_charge":{
              "id":"ch_new","object":"charge","payment_method_details":{"type":"card"},
              "balance_transaction":{"id":"txn_new","object":"balance_transaction","currency":"eur","fee":150,
                "fee_details":[{"type":"stripe_fee","amount":100,"currency":"eur"},
                               {"type":"some_future_fee","amount":50,"currency":"eur"}]}}}
            """;
        http.MakeRequestAsync(Arg.Any<StripeRequest>(), Arg.Any<CancellationToken>())
            .Returns(new StripeResponse(HttpStatusCode.OK, headers.Headers, payload));
        var log = new CapturingLogger<StripeService>();
        var service = new StripeService(Options.Create(new StripeSettings { TicketsKey = "sk_test_fees" }), log, http);

        var details = await service.GetPaymentDetailsAsync("pi_new", TestContext.Current.CancellationToken);

        details.Should().NotBeNull();
        details.StripeFee.Should().Be(1.00m);
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Message.Contains("pi_new"));
    }

    [HumansTheory]
    [InlineData("transaction", null, true)]
    [InlineData("transaction", null, false)]
    [InlineData("transaction", "", false)]
    [InlineData("transaction", "usd", false)]
    [InlineData("transaction", "eur", false)]
    [InlineData("transaction", "EUR", false)]
    [InlineData("stripe_fee", null, true)]
    [InlineData("stripe_fee", null, false)]
    [InlineData("stripe_fee", "", false)]
    [InlineData("stripe_fee", "usd", false)]
    [InlineData("stripe_fee", "eur", false)]
    [InlineData("stripe_fee", "EUR", false)]
    [InlineData("application_fee", null, true)]
    [InlineData("application_fee", null, false)]
    [InlineData("application_fee", "", false)]
    [InlineData("application_fee", "usd", false)]
    [InlineData("application_fee", "eur", false)]
    [InlineData("application_fee", "EUR", false)]
    public async Task PaymentDetails_requires_euro_fee_currency(string target, string? currency, bool omitted)
    {
        var payload = JsonNode.Parse("""
            {"id":"pi_currency","object":"payment_intent","status":"succeeded","currency":"eur","latest_charge":{
              "id":"ch_currency","object":"charge","currency":"eur","payment_method_details":{"type":"card"},
              "balance_transaction":{"id":"txn_currency","object":"balance_transaction","currency":"eur",
                "fee_details":[{"type":"stripe_fee","amount":123,"currency":"eur"},{"type":"application_fee","amount":45,"currency":"eur"}]}}}
            """)!;
        var transaction = payload["latest_charge"]!["balance_transaction"]!.AsObject();
        var fees = transaction["fee_details"]!.AsArray();
        var field = target switch
        {
            "transaction" => transaction,
            "stripe_fee" => fees[0]!.AsObject(),
            _ => fees[1]!.AsObject()
        };
        if (omitted) field.Remove("currency");
        else field["currency"] = currency;
        var http = Substitute.For<IHttpClient>();
        using var headers = new HttpResponseMessage();
        http.MakeRequestAsync(Arg.Any<StripeRequest>(), Arg.Any<CancellationToken>())
            .Returns(new StripeResponse(HttpStatusCode.OK, headers.Headers, payload.ToJsonString()));
        var service = new StripeService(Options.Create(new StripeSettings { TicketsKey = "sk_test_fees" }),
            new CapturingLogger<StripeService>(), http);
        var read = () => service.GetPaymentDetailsAsync("pi_currency", TestContext.Current.CancellationToken);

        if (!omitted && string.Equals(currency, "eur", StringComparison.OrdinalIgnoreCase))
        {
            var details = await read();
            details.Should().NotBeNull();
            details.StripeFee.Should().Be(1.23m);
            details.ApplicationFee.Should().Be(0.45m);
        }
        else
            await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*EUR*");
    }

    [HumansFact]
    public async Task ListStoreCheckoutSessions_returns_null_not_empty_when_store_key_unset()
    {
        var (svc, log) = Build();

        var result = await svc.ListStoreCheckoutSessionsAsync();

        // Null and empty mean different things here: empty is "Stripe has no sessions", which
        // would let reconciliation flag every recorded payment as an orphan.
        result.Should().BeNull();
        log.Entries.Should().Contain(e => e.Level == LogLevel.Warning);
    }

    [HumansFact]
    public void ParseStoreCheckoutEvent_returns_null_when_signing_secret_unset()
    {
        var (svc, log) = Build();

        svc.ParseStoreCheckoutEvent(SessionPayload("checkout.session.completed"), "t=1,v1=deadbeef")
            .Should().BeNull();
        log.Entries.Should().Contain(e => e.Level == LogLevel.Warning);
    }

    [HumansFact]
    public void ParseStoreCheckoutEvent_returns_null_for_an_invalid_signature()
    {
        var (svc, _) = Build(s => s.StoreWebhookSecret = TestWebhookSecret);
        var payload = SessionPayload("checkout.session.completed");

        // A correctly-shaped header signed with the wrong secret, and a header that is
        // not a signature at all. Neither may produce a parsed event.
        svc.ParseStoreCheckoutEvent(payload, Sign(payload, "whsec_wrong_secret")).Should().BeNull();
        svc.ParseStoreCheckoutEvent(payload, "not-a-signature").Should().BeNull();
    }

    [HumansTheory]
    [InlineData(0)]
    [InlineData(-0.01)]
    [InlineData(-100)]
    public async Task CreateCheckoutSession_rejects_a_non_positive_amount(decimal amountEur)
    {
        var (svc, _) = Build(s => s.StoreKey = "sk_test_store");

        var act = () => svc.CreateCheckoutSessionAsync(
            Guid.NewGuid(), amountEur, "https://x/ok", "https://x/no", null, "Order");

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [HumansFact]
    public async Task CreateCheckoutSession_rejects_an_unconfigured_store_key()
    {
        var (svc, _) = Build();

        var act = () => svc.CreateCheckoutSessionAsync(
            Guid.NewGuid(), 10m, "https://x/ok", "https://x/no", null, "Order");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [HumansTheory]
    [InlineData("checkout.session.completed", StoreCheckoutEventKind.CheckoutSessionCompleted)]
    [InlineData("checkout.session.async_payment_succeeded", StoreCheckoutEventKind.CheckoutSessionAsyncPaymentSucceeded)]
    [InlineData("checkout.session.async_payment_failed", StoreCheckoutEventKind.CheckoutSessionAsyncPaymentFailed)]
    [InlineData("checkout.session.expired", StoreCheckoutEventKind.CheckoutSessionExpired)]
    [InlineData("customer.created", StoreCheckoutEventKind.Other)]
    public void ParseStoreCheckoutEvent_categorizes_the_four_subscribed_events(
        string stripeEventType, StoreCheckoutEventKind expected)
    {
        var (svc, _) = Build(s => s.StoreWebhookSecret = TestWebhookSecret);
        var payload = SessionPayload(stripeEventType);

        svc.ParseStoreCheckoutEvent(payload, Sign(payload, TestWebhookSecret))!
            .Kind.Should().Be(expected);
    }

    [HumansTheory]
    [InlineData("eur")]
    [InlineData("EUR")]
    public void ParseStoreCheckoutEvent_projects_the_order_id_amount_and_status(string currency)
    {
        var (svc, _) = Build(s => s.StoreWebhookSecret = TestWebhookSecret);
        var orderId = Guid.NewGuid();
        var document = JsonNode.Parse(SessionPayload("checkout.session.completed", orderId))!;
        var session = document["data"]!["object"]!;
        session["currency"] = currency;
        session["presentment_details"] = new JsonObject
        {
            ["presentment_amount"] = 2199,
            ["presentment_currency"] = "usd",
        };
        var payload = document.ToJsonString();

        var parsed = svc.ParseStoreCheckoutEvent(payload, Sign(payload, TestWebhookSecret));

        parsed.Should().NotBeNull();
        parsed.EventId.Should().Be("evt_test_x");
        parsed.Session.Should().NotBeNull();
        parsed.Session!.SessionId.Should().Be("cs_test_x");
        parsed.Session.OrderId.Should().Be(orderId);
        parsed.Session.PaymentIntentId.Should().Be("pi_test_x");
        parsed.Session.AmountEur.Should().Be(19.99m);
        parsed.Session.PaymentStatus.Should().Be("paid");
    }

    [HumansTheory]
    [InlineData("usd", true)]
    [InlineData("jpy", true)]
    [InlineData(null, true)]
    [InlineData(null, false)]
    [InlineData("", true)]
    public void ParseStoreCheckoutEvent_requires_explicit_euro_currency(string? currency, bool present)
    {
        var (svc, _) = Build(s => s.StoreWebhookSecret = TestWebhookSecret);
        var document = JsonNode.Parse(SessionPayload("checkout.session.completed"))!;
        var session = document["data"]!["object"]!.AsObject();
        if (present) session["currency"] = currency;
        else session.Remove("currency");
        var payload = document.ToJsonString();

        svc.ParseStoreCheckoutEvent(payload, Sign(payload, TestWebhookSecret)).Should().BeNull();
    }

    [HumansFact]
    public void ParseStoreCheckoutEvent_leaves_the_order_id_null_when_the_metadata_is_unusable()
    {
        var (svc, _) = Build(s => s.StoreWebhookSecret = TestWebhookSecret);

        // Missing key, and a value that is not a Guid. Both must yield null rather than a
        // wrong order id -- the caller cannot tell a mis-parsed id from a real one.
        foreach (var metadata in new[] { "{}", """{"humans_store_order_id":"not-a-guid"}""" })
        {
            var payload = SessionPayload("checkout.session.completed", metadataJson: metadata);
            svc.ParseStoreCheckoutEvent(payload, Sign(payload, TestWebhookSecret))!
                .Session!.OrderId.Should().BeNull();
        }
    }

    private static string SessionPayload(
        string eventType, Guid? orderId = null, string? metadataJson = null)
    {
        var metadata = metadataJson
            ?? "{\"humans_store_order_id\":\"" + (orderId ?? Guid.NewGuid()) + "\"}";

        return "{\"id\":\"evt_test_x\",\"object\":\"event\",\"type\":\"" + eventType + "\","
            + "\"data\":{\"object\":{"
            + "\"id\":\"cs_test_x\",\"object\":\"checkout.session\","
            + "\"payment_intent\":\"pi_test_x\",\"amount_total\":1999,\"currency\":\"eur\","
            + "\"payment_status\":\"paid\",\"created\":1750000000,"
            + "\"metadata\":" + metadata + "}}}";
    }

    /// <summary>
    /// Builds a real <c>Stripe-Signature</c> header the way Stripe does, so the parse path runs
    /// the SDK's genuine verification rather than a bypass. Mirrors
    /// <see cref="StripeSignatureSanityTest"/>, which is what proves this helper itself is right.
    /// </summary>
    private static string Sign(string payload, string secret)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{ts}.{payload}"));
        return $"t={ts},v1={Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
