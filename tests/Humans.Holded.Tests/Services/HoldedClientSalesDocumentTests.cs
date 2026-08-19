using System.Net;
using System.Text;
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

    [HumansFact]
    public async Task GetSalesDocumentAsync_ParsesTotalsAndKeepsTheRawBody()
    {
        // Decimals arrive as strings, per the connector invariant.
        const string json = """
        {"id":"doc-9","document_number":"2026-0007","subtotal":"160.00","tax":"21.00",
         "total":"181.00","status":"pending"}
        """;
        var handler = new StubHandler(req =>
        {
            req.RequestUri!.PathAndQuery.Should().Be("/api/v2/invoices/doc-9");
            return Respond(HttpStatusCode.OK, json);
        });

        var doc = await Make(handler).GetSalesDocumentAsync(
            HoldedSalesDocumentKind.Invoice, "doc-9", Xunit.TestContext.Current.CancellationToken);

        doc.Id.Should().Be("doc-9");
        doc.DocNumber.Should().Be("2026-0007");
        doc.Subtotal.Should().Be(160.00m);
        doc.Tax.Should().Be(21.00m);
        doc.Total.Should().Be(181.00m);
        doc.Status.Should().Be("pending");
        doc.RawJson.Should().Be(json);
    }

    [HumansFact]
    public async Task UpsertContactAsync_SendsTaxCodeAndBillingAddressForAClient()
    {
        string? body = null;
        var handler = new StubHandler(req =>
        {
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Respond(HttpStatusCode.Created, """{"id":"contact-1"}""");
        });

        await Make(handler).UpsertContactAsync(new HoldedContactInput
        {
            Name = "Camp Frio SL",
            Type = "client",
            TaxCode = "B12345678",
            Address = "Calle Falsa 1",
            CountryCode = "ES",
            Email = "lead@campfrio.example",
        }, Xunit.TestContext.Current.CancellationToken);

        body.Should().Contain("\"type\":\"client\"");
        body.Should().Contain("\"code\":\"B12345678\"");
        body.Should().Contain("\"country_code\":\"ES\"");
        body.Should().Contain("\"address\":\"Calle Falsa 1\"");
    }

    [HumansFact]
    public async Task UpsertContactAsync_OmitsBillAddressWhenNoAddressIsHeld()
    {
        // Sending an object of nulls would blank an address already on the contact.
        string? body = null;
        var handler = new StubHandler(req =>
        {
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Respond(HttpStatusCode.Created, """{"id":"contact-1"}""");
        });

        await Make(handler).UpsertContactAsync(
            new HoldedContactInput { Name = "Supplier", Type = "creditor" },
            Xunit.TestContext.Current.CancellationToken);

        body.Should().Contain("\"bill_address\":null");
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
