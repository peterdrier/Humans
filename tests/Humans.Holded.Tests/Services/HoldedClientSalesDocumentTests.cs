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
    public async Task UpsertContactAsync_OmitsEveryFieldItHoldsNoValueFor()
    {
        // Holded applies every key a PUT carries, so a null blanks the stored value. Finance's
        // creditor update supplies only name/trade name/type/iban — the tax code, email and billing
        // address maintained in Holded for that contact must survive it untouched.
        string? body = null;
        var handler = new StubHandler(req =>
        {
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Respond(HttpStatusCode.OK, """{"id":"contact-1"}""");
        });

        await Make(handler).UpsertContactAsync(
            new HoldedContactInput
            {
                Name = "Supplier",
                Type = "creditor",
                Iban = "ES9121000418450200051332",
                ExistingContactId = "contact-1",
            },
            Xunit.TestContext.Current.CancellationToken);

        body.Should().NotContain("code");
        body.Should().NotContain("email");
        body.Should().NotContain("bill_address");
        body.Should().NotContain("trade_name");
        body.Should().NotContain("null");
        // An update omits type entirely (see HoldedClientContactTests.UpsertContact_update_omits_type) —
        // sending it flips a manually-created contact's type on Holded's side.
        body.Should().NotContain("\"type\"");
        // What it does mean to send still goes.
        body.Should().Contain("\"name\":\"Supplier\"");
        body.Should().Contain("\"iban\":\"ES9121000418450200051332\"");
    }

    [HumansFact]
    public async Task UpsertContactAsync_StillSendsTheFieldsItDoesHold()
    {
        // Omission must not swallow a value a caller deliberately supplied.
        string? body = null;
        var handler = new StubHandler(req =>
        {
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Respond(HttpStatusCode.Created, """{"id":"contact-1"}""");
        });

        await Make(handler).UpsertContactAsync(new HoldedContactInput
        {
            Name = "Camp Frio SL",
            TradeName = "Camp Frio",
            Type = "client",
            TaxCode = "B12345678",
            Email = "lead@campfrio.example",
            Address = "Calle Falsa 1",
            CountryCode = "ES",
        }, Xunit.TestContext.Current.CancellationToken);

        body.Should().Contain("\"trade_name\":\"Camp Frio\"");
        body.Should().Contain("\"code\":\"B12345678\"");
        body.Should().Contain("\"email\":\"lead@campfrio.example\"");
        body.Should().Contain("\"address\":\"Calle Falsa 1\"");
        body.Should().Contain("\"country_code\":\"ES\"");
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
