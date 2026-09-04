using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Humans.Stripe.Contracts;
using Humans.Stripe.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
// Compilation-unit level, so this binds to the SDK's global::Stripe rather than to the
// enclosing Humans.Stripe section namespace. See StripeService.cs.
using Stripe;

namespace Humans.Stripe.Tests.Services;

/// <summary>
/// The connector's one behavioural rule: every read returns <c>null</c> when Stripe cannot be
/// asked, and the write throws. Both halves are reachable without a network call — each guard
/// runs before the <c>StripeClient</c> is constructed — so this pins them from a unit test.
/// A read that throws where its siblings return null is the defect these tests exist to catch.
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

    // ── Reads return null, never throw ──────────────────────────────────────

    [HumansFact]
    public async Task GetPaymentDetails_returns_null_when_tickets_key_unset()
    {
        var (svc, log) = Build();

        var result = await svc.GetPaymentDetailsAsync("pi_test_x");

        result.Should().BeNull();
        log.Entries.Should().Contain(e => e.Level == LogLevel.Warning);
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

    // ── The write throws ────────────────────────────────────────────────────

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

    // ── Categorisation and session projection ───────────────────────────────

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

    [HumansFact]
    public void ParseStoreCheckoutEvent_projects_the_order_id_amount_and_status()
    {
        var (svc, _) = Build(s => s.StoreWebhookSecret = TestWebhookSecret);
        var orderId = Guid.NewGuid();
        var payload = SessionPayload("checkout.session.completed", orderId);

        var parsed = svc.ParseStoreCheckoutEvent(payload, Sign(payload, TestWebhookSecret));

        parsed.Should().NotBeNull();
        parsed!.EventId.Should().Be("evt_test_x");
        parsed.Session.Should().NotBeNull();
        parsed.Session!.SessionId.Should().Be("cs_test_x");
        parsed.Session.OrderId.Should().Be(orderId);
        parsed.Session.PaymentIntentId.Should().Be("pi_test_x");
        parsed.Session.AmountEur.Should().Be(19.99m);
        parsed.Session.PaymentStatus.Should().Be("paid");
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

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string SessionPayload(
        string eventType, Guid? orderId = null, string? metadataJson = null)
    {
        var metadata = metadataJson
            ?? "{\"humans_store_order_id\":\"" + (orderId ?? Guid.NewGuid()) + "\"}";

        return "{\"id\":\"evt_test_x\",\"object\":\"event\",\"type\":\"" + eventType + "\","
            + "\"data\":{\"object\":{"
            + "\"id\":\"cs_test_x\",\"object\":\"checkout.session\","
            + "\"payment_intent\":\"pi_test_x\",\"amount_total\":1999,"
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
