using System.Net;
using System.Text;
using AwesomeAssertions;
using Humans.Holded.Contracts;
using Humans.Holded.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;

namespace Humans.Holded.Tests.Services;

public class HoldedClientTests
{
    private static HoldedClient Make(StubHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.holded.com") },
            Options.Create(new HoldedClientOptions { ApiKey = "test-key" }),
            NullLogger<HoldedClient>.Instance,
            new HoldedCallLog(),
            new FakeClock(Instant.FromUtc(2026, 8, 10, 12, 0)));

    [HumansFact]
    public async Task CreatePurchaseDocumentAsync_PostsExpectedJson_AndReturnsId()
    {
        string? capturedBody = null;
        var handler = new StubHandler(req =>
        {
            req.Method.Method.Should().Be("POST");
            req.RequestUri!.PathAndQuery.Should().Be("/api/v2/purchases");
            req.Headers.Authorization!.Scheme.Should().Be("Bearer");
            req.Headers.Authorization!.Parameter.Should().Be("test-key");
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Respond(HttpStatusCode.Created, """{"id":"doc-123"}""");
        });

        var client = Make(handler);
        var id = await client.CreatePurchaseDocumentAsync(new HoldedPurchaseDocumentInput
        {
            ContactId = "contact-1",
            ContactName = "Alice",
            Date = Instant.FromUtc(2026, 5, 10, 0, 0),
            Lines = [new() { Description = "Train", Amount = 19.52m, AccountId = "acc-629001" }]
        }, Xunit.TestContext.Current.CancellationToken);

        id.Should().Be("doc-123");
        capturedBody.Should().Contain("\"contact_id\":\"contact-1\"");
        capturedBody.Should().Contain("\"date\":\"2026-05-10\"");
        capturedBody.Should().Contain("\"account\":\"acc-629001\"");
        // Tags are dead on the write side (Peter, 2026-08-10) — the mapped account books the doc
        // to the right category from creation, so no tags are sent at all.
        capturedBody.Should().NotContain("tags");
    }

    [HumansFact]
    public async Task GetPurchaseDocumentAsync_ParsesResponse()
    {
        var json = """
        {
          "id":"doc-123","document_number":"F260009",
          "subtotal":"19.52","tax":"0.00","total":"19.52",
          "payments_total":"19.52","payments_pending":"0.00",
          "approved_at":"2026-05-10T09:00:00",
          "tags":["camp-build-camp"]
        }
        """;
        var handler = new StubHandler(req =>
        {
            req.Method.Method.Should().Be("GET");
            req.RequestUri!.PathAndQuery
                .Should().Be("/api/v2/purchases/doc-123");
            return Respond(HttpStatusCode.OK, json);
        });

        var client = Make(handler);
        var doc = await client.GetPurchaseDocumentAsync("doc-123", Xunit.TestContext.Current.CancellationToken);

        doc.Id.Should().Be("doc-123");
        doc.DocNumber.Should().Be("F260009");
        doc.PaymentsPending.Should().Be(0);
        doc.ApprovedAt.Should().NotBeNull();
        doc.Tags.Should().ContainSingle("camp-build-camp");
    }

    [HumansFact]
    public async Task GetPurchaseDocumentAsync_surfaces_an_unreadable_body_as_permanent_not_a_raw_parse_throw()
    {
        // Callers handle only IHoldedClient's two typed exceptions; a raw parse throw from a bad
        // stored value would escape the client and leak an Infrastructure detail upward.
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, """{"id":42,"document_number":"F001"}"""));
        var client = Make(handler);

        var act = async () => await client.GetPurchaseDocumentAsync("doc-123", Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HoldedPermanentException>();
    }

    [HumansFact]
    public async Task GetPurchaseDocumentAsync_404Throws_HoldedPermanent()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.NotFound, "{}"));
        var client = Make(handler);

        var act = async () => await client.GetPurchaseDocumentAsync("missing", Xunit.TestContext.Current.CancellationToken);

        var ex = await act.Should().ThrowAsync<HoldedPermanentException>();
        ex.Which.StatusCode.Should().Be(404);
    }

    [HumansFact]
    public async Task PermanentFailure_MasksAnIbanEchoedBackInTheResponseBody()
    {
        // The message becomes the outbox LastError, the audit description and the finance-admin
        // card. A 4xx on a contact upsert can echo the IBAN we just sent
        // (memory/code/iban-mask-in-logs.md). ResponseBody keeps the untouched body.
        var body = """{"status":0,"info":"invalid iban ES9121000418450200051332"}""";
        var handler = new StubHandler(_ => Respond(HttpStatusCode.BadRequest, body));
        var client = Make(handler);

        var act = async () => await client.GetPurchaseDocumentAsync("doc-1", Xunit.TestContext.Current.CancellationToken);

        var ex = await act.Should().ThrowAsync<HoldedPermanentException>();
        ex.Which.Message.Should().NotContain("ES9121000418450200051332");
        ex.Which.Message.Should().Contain("ES91****332");
        ex.Which.ResponseBody.Should().Be(body);
    }

    [HumansFact]
    public async Task GetPurchaseDocumentAsync_500Throws_HoldedTransient()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.ServiceUnavailable, ""));
        var client = Make(handler);

        var act = async () => await client.GetPurchaseDocumentAsync("doc-123", Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HoldedTransientException>();
    }

    [HumansFact]
    public async Task PayPurchaseDocumentAsync_PostsTheAmountAsAStringAndReturnsThePaymentId()
    {
        string? capturedBody = null;
        var handler = new StubHandler(req =>
        {
            req.Method.Method.Should().Be("POST");
            req.RequestUri!.PathAndQuery.Should().Be("/api/v2/purchases/doc-123/payments");
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Respond(HttpStatusCode.Created, """{"id":"pay-9"}""");
        });

        var id = await Make(handler).PayPurchaseDocumentAsync(
            "doc-123", 18m, "treasury-1", new LocalDate(2026, 8, 25), "SEPA payout E1",
            Xunit.TestContext.Current.CancellationToken);

        id.Should().Be("pay-9");
        // v2 rejects a JSON number here — the amount has to go over the wire as a decimal string.
        capturedBody.Should().Contain("\"amount\":\"18.00\"");
        capturedBody.Should().Contain("\"treasury_id\":\"treasury-1\"");
        capturedBody.Should().Contain("\"date\":\"2026-08-25\"");
    }

    [HumansFact]
    public async Task PayPurchaseDocumentAsync_NoTreasuryAccount_OmitsTheKeyRatherThanSendingNull()
    {
        // A null treasury_id is not "use the default" to Holded — the key is simply left out.
        string? capturedBody = null;
        var handler = new StubHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Respond(HttpStatusCode.Created, """{"id":"pay-9"}""");
        });

        await Make(handler).PayPurchaseDocumentAsync(
            "doc-123", 5m, "", new LocalDate(2026, 8, 25), null,
            Xunit.TestContext.Current.CancellationToken);

        capturedBody.Should().NotContain("treasury_id");
        capturedBody.Should().NotContain("description");
    }

    [HumansFact]
    public async Task PayPurchaseDocumentAsync_ResponseWithoutAPaymentId_ReturnsAnUnconfirmedRef()
    {
        // A 2xx means the payment posted. Throwing would lose it from the record and let the caller
        // pay again — the sentinel keeps it, naming the document a human has to check in Holded.
        var handler = new StubHandler(_ => Respond(HttpStatusCode.Created, "{}"));

        var id = await Make(handler).PayPurchaseDocumentAsync(
            "doc-123", 5m, "treasury-1", new LocalDate(2026, 8, 25), null,
            Xunit.TestContext.Current.CancellationToken);

        id.Should().Be("unconfirmed:doc-123");
    }

    [HumansFact]
    public async Task PayPurchaseDocumentAsync_BlankPaymentId_ReturnsAnUnconfirmedRef()
    {
        // An empty ref would defeat both the sentinel and the non-empty-refs re-booking gate.
        var handler = new StubHandler(_ => Respond(HttpStatusCode.Created, """{"id":""}"""));

        var id = await Make(handler).PayPurchaseDocumentAsync(
            "doc-123", 5m, "treasury-1", new LocalDate(2026, 8, 25), null,
            Xunit.TestContext.Current.CancellationToken);

        id.Should().Be("unconfirmed:doc-123");
    }

    [HumansFact]
    public async Task PayPurchaseDocumentAsync_UnparseableResponseBody_ReturnsAnUnconfirmedRef()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.Created, "not json"));

        var id = await Make(handler).PayPurchaseDocumentAsync(
            "doc-123", 5m, "treasury-1", new LocalDate(2026, 8, 25), null,
            Xunit.TestContext.Current.CancellationToken);

        id.Should().Be("unconfirmed:doc-123");
    }

    private static HttpResponseMessage Respond(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
