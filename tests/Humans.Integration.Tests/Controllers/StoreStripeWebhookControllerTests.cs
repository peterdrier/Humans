using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Xunit;

namespace Humans.Integration.Tests.Controllers;

public class StoreStripeWebhookControllerTests : IntegrationTestBase
{
    public StoreStripeWebhookControllerTests(HumansWebApplicationFactory factory) : base(factory) { }

    [HumansFact(Timeout = 30000)]
    public async Task Webhook_with_invalid_signature_returns_400()
    {
        var payload = BuildCheckoutSessionCompletedJson(
            sessionId: "cs_test_invalid_sig",
            paymentIntentId: "pi_test_invalid_sig",
            amountTotalMinorUnits: 1000,
            metadataOrderId: Guid.NewGuid().ToString());

        var resp = await PostWithSignatureAsync(payload, signatureHeader: "t=1700000000,v1=deadbeef");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [HumansFact(Timeout = 30000)]
    public async Task Webhook_with_unknown_event_type_is_a_200_no_op()
    {
        var payload = BuildEventJson(eventType: "customer.subscription.created", dataObjectJson: "{}");
        var (sig, _) = SignPayload(payload, HumansWebApplicationFactory.TestStripeWebhookSecret);

        var resp = await PostWithSignatureAsync(payload, sig);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [HumansFact(Timeout = 30000)]
    public async Task Webhook_checkout_session_completed_records_payment()
    {
        var orderId = await SeedOpenOrderAsync();
        const string paymentIntentId = "pi_test_record_once";

        var payload = BuildCheckoutSessionCompletedJson(
            sessionId: "cs_test_record_once",
            paymentIntentId: paymentIntentId,
            amountTotalMinorUnits: 4250,
            metadataOrderId: orderId.ToString());
        var (sig, _) = SignPayload(payload, HumansWebApplicationFactory.TestStripeWebhookSecret);

        var resp = await PostWithSignatureAsync(payload, sig);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var payment = await db.StorePayments.AsNoTracking()
            .SingleOrDefaultAsync(p => p.StripePaymentIntentId == paymentIntentId);
        payment.Should().NotBeNull();
        payment!.OrderId.Should().Be(orderId);
        payment.AmountEur.Should().Be(42.50m);
        payment.Method.Should().Be(StorePaymentMethod.Stripe);
    }

    [HumansFact(Timeout = 30000)]
    public async Task Webhook_duplicate_event_does_not_double_record_payment()
    {
        var orderId = await SeedOpenOrderAsync();
        const string paymentIntentId = "pi_test_dedup";

        var payload = BuildCheckoutSessionCompletedJson(
            sessionId: "cs_test_dedup",
            paymentIntentId: paymentIntentId,
            amountTotalMinorUnits: 1000,
            metadataOrderId: orderId.ToString());
        var (sig, _) = SignPayload(payload, HumansWebApplicationFactory.TestStripeWebhookSecret);

        (await PostWithSignatureAsync(payload, sig)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostWithSignatureAsync(payload, sig)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var matches = await db.StorePayments.AsNoTracking()
            .Where(p => p.StripePaymentIntentId == paymentIntentId)
            .CountAsync();
        matches.Should().Be(1);
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private async Task<HttpResponseMessage> PostWithSignatureAsync(string payload, string signatureHeader)
    {
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var req = new HttpRequestMessage(HttpMethod.Post, "/Store/StripeWebhook")
        {
            Content = content,
        };
        req.Headers.Add("Stripe-Signature", signatureHeader);
        return await Client.SendAsync(req);
    }

    private async Task<Guid> SeedOpenOrderAsync()
    {
        // Sign in once as a barrio lead persona to trigger the dev camp/season seeding,
        // then seed the order via DbContext. The webhook itself is anonymous, so this
        // login flow is just a convenient way to get the foreign-key prerequisites in place.
        using (var seedClient = Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        }))
        {
            await Factory.SignInAsFullyOnboardedAsync(seedClient, new DevPersona("barrio-1-lead"));
        }

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var seasonId = await db.Set<CampSeason>().AsNoTracking()
            .Where(s => s.Camp.Slug == "barrio-1")
            .Select(s => s.Id).FirstOrDefaultAsync();
        if (seasonId == Guid.Empty)
            throw new InvalidOperationException("Dev seed didn't produce a CampSeason for barrio-1.");

        var orderId = Guid.NewGuid();
        var now = SystemClock.Instance.GetCurrentInstant();
        db.StoreOrders.Add(new StoreOrder
        {
            Id = orderId,
            CampSeasonId = seasonId,
            State = StoreOrderState.Open,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return orderId;
    }

    /// <summary>
    /// Builds a minimal Stripe Event JSON with the supplied data.object payload.
    /// </summary>
    private static string BuildEventJson(string eventType, string dataObjectJson)
    {
        return $$"""
        {
          "id": "evt_test_{{Guid.NewGuid():N}}",
          "object": "event",
          "api_version": "2024-04-10",
          "created": {{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}},
          "type": "{{eventType}}",
          "data": { "object": {{dataObjectJson}} }
        }
        """;
    }

    private static string BuildCheckoutSessionCompletedJson(
        string sessionId, string paymentIntentId, long amountTotalMinorUnits, string metadataOrderId)
    {
        var sessionJson = $$"""
        {
          "id": "{{sessionId}}",
          "object": "checkout.session",
          "payment_intent": "{{paymentIntentId}}",
          "amount_total": {{amountTotalMinorUnits.ToString(CultureInfo.InvariantCulture)}},
          "currency": "eur",
          "metadata": { "humans_store_order_id": "{{metadataOrderId}}" }
        }
        """;
        return BuildEventJson("checkout.session.completed", sessionJson);
    }

    /// <summary>
    /// Computes a valid Stripe-Signature header (scheme v1) per
    /// https://docs.stripe.com/webhooks#verify-manually.
    /// </summary>
    private static (string Header, long Timestamp) SignPayload(string payload, string secret)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signedPayload = $"{ts.ToString(CultureInfo.InvariantCulture)}.{payload}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload));
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return ($"t={ts.ToString(CultureInfo.InvariantCulture)},v1={hex}", ts);
    }
}
