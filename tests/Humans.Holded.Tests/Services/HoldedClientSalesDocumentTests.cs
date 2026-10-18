using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Humans.Holded.Contracts;
using Humans.Holded.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace Humans.Holded.Tests.Services;

/// <summary>
/// Wire shape of the v2 sales-document pipeline (nobodies-collective/Humans#1029). Field names
/// are locked against <c>https://api.holded.com/openapi/api2.json</c> and a live read of the
/// account's documents (2026-08-19): <c>items[].account</c> carries the chart account's
/// <b>id</b>, and <c>items[].taxes</c> carries sales tax keys such as <c>s_iva_21</c>.
/// </summary>
public class HoldedClientSalesDocumentTests
{
    private static HoldedClient Make(StubHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.holded.com") },
            Options.Create(new HoldedClientOptions { ApiKey = "test-key" }),
            NullLogger<HoldedClient>.Instance,
            new HoldedCallLog(),
            new FakeClock(Instant.FromUtc(2026, 9, 1, 12, 0)));

    private static HoldedSalesDocumentInput Input() => new()
    {
        ContactId = "contact-1",
        ContactName = "Camp Frio SL",
        Date = Instant.FromUtc(2026, 9, 1, 0, 0),
        Description = "Store order",
        Tags = ["humans-order-1"],
        Lines =
        [
            new() { Name = "Ice", Units = 20m, Price = 5m, Taxes = ["s_iva_21"], AccountId = "acct-ice" },
            new() { Name = "Ice — refundable deposit", Units = 20m, Price = 3m, Taxes = ["s_iva_0"], AccountId = "acct-dep" },
        ],
    };

    [HumansTheory]
    [InlineData(HoldedSalesDocumentKind.Invoice, "/api/v2/invoices")]
    [InlineData(HoldedSalesDocumentKind.SalesReceipt, "/api/v2/sales-receipts")]
    public async Task CreateSalesDocumentAsync_PostsExpectedJson_AndReturnsId(
        HoldedSalesDocumentKind kind, string expectedPath)
    {
        string? body = null;
        var handler = new StubHandler(req =>
        {
            req.Method.Method.Should().Be("POST");
            req.RequestUri!.PathAndQuery.Should().Be(expectedPath);
            req.Headers.Authorization!.Parameter.Should().Be("test-key");
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Respond(HttpStatusCode.Created, """{"id":"doc-9"}""");
        });

        var id = await Make(handler).CreateSalesDocumentAsync(
            kind, Input(), Xunit.TestContext.Current.CancellationToken);

        id.Should().Be("doc-9");
        body.Should().Contain("\"contact_id\":\"contact-1\"");
        body.Should().Contain("\"date\":\"2026-09-01\"");
        body.Should().Contain("\"currency\":\"EUR\"");
        body.Should().Contain("\"account\":\"acct-ice\"");
        body.Should().Contain("\"account\":\"acct-dep\"");
        body.Should().Contain("\"taxes\":[\"s_iva_21\"]");
        body.Should().Contain("\"taxes\":[\"s_iva_0\"]");
        // The tag is what makes this document findable again: v2's list endpoints return `tags`
        // but not `notes`, so it — not the note beside it — carries the originating order.
        body.Should().Contain("\"tags\":[\"humans-order-1\"]");
    }

    [HumansTheory]
    [InlineData(HoldedSalesDocumentKind.Invoice, "/api/v2/invoices?limit=200")]
    [InlineData(HoldedSalesDocumentKind.SalesReceipt, "/api/v2/sales-receipts?limit=200")]
    public async Task FindSalesDocumentIdsByTagAsync_ReturnsOnlyTheTaggedDocuments(
        HoldedSalesDocumentKind kind, string expectedPath)
    {
        // v2 has no server-side tag filter, so the whole collection is walked and matched here.
        const string json = """
        {"items":[
          {"id":"doc-1","tags":["humans-order-1"]},
          {"id":"doc-2","tags":["humans-order-2"]},
          {"id":"doc-3","tags":[]},
          {"id":"doc-4","tags":["other","humans-order-2"]}
        ],"has_more":false}
        """;
        var handler = new StubHandler(req =>
        {
            req.Method.Method.Should().Be("GET");
            req.RequestUri!.PathAndQuery.Should().Be(expectedPath);
            return Respond(HttpStatusCode.OK, json);
        });

        var ids = await Make(handler).FindSalesDocumentIdsByTagAsync(
            kind, "humans-order-2", Xunit.TestContext.Current.CancellationToken);

        ids.Should().Equal("doc-2", "doc-4");
    }

    [HumansFact]
    public async Task FindSalesDocumentIdsByTagAsync_RefusesAMatchWithNoId()
    {
        // An empty result reads as "nothing issued yet" and lets a second document be created,
        // so a match that cannot be identified must fail the search rather than be dropped.
        var handler = new StubHandler(_ => Respond(
            HttpStatusCode.OK,
            """{"items":[{"tags":["humans-order-1"]}],"has_more":false}"""));

        var act = async () => await Make(handler).FindSalesDocumentIdsByTagAsync(
            HoldedSalesDocumentKind.Invoice, "humans-order-1", Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HoldedPermanentException>();
    }

    [HumansTheory]
    [InlineData(HoldedSalesDocumentKind.Invoice, "/api/v2/invoices/doc-9/approve")]
    [InlineData(HoldedSalesDocumentKind.SalesReceipt, "/api/v2/sales-receipts/doc-9/approve")]
    public async Task ApproveSalesDocumentAsync_PostsToTheApprovePath(
        HoldedSalesDocumentKind kind, string expectedPath)
    {
        var handler = new StubHandler(req =>
        {
            req.Method.Method.Should().Be("POST");
            req.RequestUri!.PathAndQuery.Should().Be(expectedPath);
            return Respond(HttpStatusCode.OK, "{}");
        });

        await Make(handler).ApproveSalesDocumentAsync(
            kind, "doc-9", Xunit.TestContext.Current.CancellationToken);
    }

    [HumansTheory]
    [InlineData(HoldedSalesDocumentKind.Invoice, "EUR")]
    [InlineData(HoldedSalesDocumentKind.Invoice, "eur")]
    [InlineData(HoldedSalesDocumentKind.SalesReceipt, "EUR")]
    [InlineData(HoldedSalesDocumentKind.SalesReceipt, "eur")]
    public async Task GetSalesDocumentAsync_ParsesTotalsAndKeepsTheRawBody(HoldedSalesDocumentKind kind, string currency)
    {
        // Decimals arrive as strings, per the connector invariant.
        var json = $$"""
        {"id":"doc-9","document_number":"2026-0007","subtotal":"160.00","tax":"21.00",
         "total":"181.00","status":"pending","currency":"{{currency}}"}
        """;
        var handler = new StubHandler(req =>
        {
            req.RequestUri!.PathAndQuery.Should().Be(kind == HoldedSalesDocumentKind.Invoice
                ? "/api/v2/invoices/doc-9" : "/api/v2/sales-receipts/doc-9");
            return Respond(HttpStatusCode.OK, json);
        });

        var doc = await Make(handler).GetSalesDocumentAsync(
            kind, "doc-9", Xunit.TestContext.Current.CancellationToken);

        doc.Id.Should().Be("doc-9");
        doc.DocNumber.Should().Be("2026-0007");
        doc.Subtotal.Should().Be(160.00m);
        doc.Tax.Should().Be(21.00m);
        doc.Total.Should().Be(181.00m);
        doc.Status.Should().Be("pending");
        doc.RawJson.Should().Be(json);
    }

    [HumansTheory]
    [InlineData(HoldedSalesDocumentKind.Invoice, "{\"currency\":\"EUR\"}")]
    [InlineData(HoldedSalesDocumentKind.Invoice, "{\"total\":null,\"currency\":\"EUR\"}")]
    [InlineData(HoldedSalesDocumentKind.SalesReceipt, "{\"currency\":\"EUR\"}")]
    [InlineData(HoldedSalesDocumentKind.SalesReceipt, "{\"total\":null,\"currency\":\"EUR\"}")]
    public async Task GetSalesDocumentAsync_RejectsAnAbsentTotal(
        HoldedSalesDocumentKind kind, string json)
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, json));

        var act = async () => await Make(handler).GetSalesDocumentAsync(
            kind, "doc-9", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HoldedPermanentException>();
    }

    [HumansTheory]
    [InlineData(HoldedSalesDocumentKind.Invoice)]
    [InlineData(HoldedSalesDocumentKind.SalesReceipt)]
    public async Task GetSalesDocumentAsync_PreservesAnExplicitZeroTotal(HoldedSalesDocumentKind kind)
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, "{\"total\":\"0.00\",\"currency\":\"EUR\"}"));

        var document = await Make(handler).GetSalesDocumentAsync(
            kind, "doc-9", TestContext.Current.CancellationToken);

        document.Total.Should().Be(0m);
    }

    [HumansTheory]
    [InlineData(HoldedSalesDocumentKind.Invoice, null, false)]
    [InlineData(HoldedSalesDocumentKind.Invoice, null, true)]
    [InlineData(HoldedSalesDocumentKind.Invoice, " ", true)]
    [InlineData(HoldedSalesDocumentKind.Invoice, "USD", true)]
    [InlineData(HoldedSalesDocumentKind.SalesReceipt, null, false)]
    [InlineData(HoldedSalesDocumentKind.SalesReceipt, null, true)]
    [InlineData(HoldedSalesDocumentKind.SalesReceipt, " ", true)]
    [InlineData(HoldedSalesDocumentKind.SalesReceipt, "USD", true)]
    public async Task GetSalesDocumentAsync_RequiresExplicitEuroCurrency(
        HoldedSalesDocumentKind kind, string? currency, bool present)
    {
        var document = JsonNode.Parse("""{"id":"doc-9","subtotal":"160.00","tax":"21.00","total":"181.00","draft":false}""")!.AsObject();
        if (present) document["currency"] = currency;
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, document.ToJsonString()));

        var act = async () => await Make(handler).GetSalesDocumentAsync(kind, "doc-9", TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<HoldedPermanentException>()).Which.Message.Should().Contain("currency");
    }

    [HumansFact]
    public async Task CreateSalesDocumentAsync_OmitsTheContactOnAReceipt()
    {
        // A sales receipt carries no counterparty; `"contact_id":null` is not the same as absent.
        string? body = null;
        var handler = new StubHandler(req =>
        {
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Respond(HttpStatusCode.Created, """{"id":"doc-9"}""");
        });

        await Make(handler).CreateSalesDocumentAsync(
            HoldedSalesDocumentKind.SalesReceipt,
            Input() with { ContactId = null },
            Xunit.TestContext.Current.CancellationToken);

        body.Should().NotContain("contact_id");
        body.Should().Contain("\"contact_name\":\"Camp Frio SL\"");
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
