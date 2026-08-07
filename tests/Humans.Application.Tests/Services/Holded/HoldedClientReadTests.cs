using System.Net;
using System.Text;
using AwesomeAssertions;
using Humans.Application.Interfaces.Holded;
using Humans.Infrastructure.Services.Holded;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Application.Tests.Services.Holded;

public class HoldedClientReadTests
{
    private static HoldedClient Make(StubHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.holded.com") },
            Options.Create(new HoldedClientOptions { ApiKey = "test-key" }),
            NullLogger<HoldedClient>.Instance);

    [HumansFact]
    public async Task ListExpenseAccounts_parses_num_and_name()
    {
        var json = """[{"id":"a1","name":"Otros servicios","accountNum":62900000}]""";
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, json));

        var client = Make(handler);
        var accounts = await client.ListExpenseAccountsAsync(Xunit.TestContext.Current.CancellationToken);

        accounts.Should().HaveCount(1);
        accounts[0].Id.Should().Be("a1");
        accounts[0].Name.Should().Be("Otros servicios");
        accounts[0].AccountNum.Should().Be(62900000);
    }

    [HumansFact]
    public async Task ListPurchaseDocumentsPage_parses_lines_account_and_tags()
    {
        var json = """
        [
          {
            "id":"doc-1","docNumber":"F001","contactName":"Alice",
            "date":1779141600,"approvedAt":1779228000,
            "subtotal":100.0,"tax":21.0,"total":121.0,
            "currency":"eur","tags":["adminstaff"],
            "products":[
              {"price":100.0,"account":"acc-629","tags":["adminstaff"]}
            ]
          }
        ]
        """;
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, json));

        var client = Make(handler);
        var docs = await client.ListPurchaseDocumentsPageAsync(1, 10, Xunit.TestContext.Current.CancellationToken);

        docs.Should().HaveCount(1);
        var doc = docs[0];
        doc.Id.Should().Be("doc-1");
        doc.Date.Should().Be(Instant.FromUnixTimeSeconds(1779141600));
        doc.ApprovedAt.Should().Be(Instant.FromUnixTimeSeconds(1779228000));
        doc.Tags.Should().ContainSingle("adminstaff");

        doc.Lines.Should().HaveCount(1);
        var line = doc.Lines[0];
        line.AccountId.Should().Be("acc-629");
        line.Tags.Should().ContainSingle("adminstaff");
        line.Amount.Should().Be(100.0m);
    }

    [HumansFact]
    public async Task ListPurchaseDocumentsPage_puts_page_and_limit_in_query_string()
    {
        string? capturedQuery = null;
        var handler = new StubHandler(req =>
        {
            capturedQuery = req.RequestUri!.Query;
            return Respond(HttpStatusCode.OK, "[]");
        });

        var client = Make(handler);
        await client.ListPurchaseDocumentsPageAsync(page: 3, limit: 50, ct: Xunit.TestContext.Current.CancellationToken);

        capturedQuery.Should().Contain("page=3");
        capturedQuery.Should().Contain("limit=50");
    }

    [HumansFact]
    public async Task ListPurchaseDocumentsPage_treats_a_non_array_products_as_no_lines()
    {
        // Holded sends an absent sub-record as an empty array (#994) and is equally free to send an
        // absent collection as something other than an array; AsArray() throws on both.
        var json = """[{"id":"doc-1","docNumber":"F001","products":{},"tags":""}]""";
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var docs = await client.ListPurchaseDocumentsPageAsync(1, 10, Xunit.TestContext.Current.CancellationToken);

        docs.Should().ContainSingle();
        docs[0].Lines.Should().BeEmpty();
        docs[0].Tags.Should().BeEmpty();
    }

    [HumansFact]
    public async Task ListPurchaseDocumentsPage_surfaces_an_unreadable_doc_as_permanent_not_a_raw_parse_throw()
    {
        // Callers handle only IHoldedClient's two typed exceptions; a raw parse throw from a bad
        // stored value would escape the client and leak an Infrastructure detail upward.
        var client = Make(new StubHandler(_ => Respond(
            HttpStatusCode.OK, """[{"id":42,"docNumber":"F001"}]""")));

        var act = async () => await client.ListPurchaseDocumentsPageAsync(1, 10, Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HoldedPermanentException>();
    }

    [HumansFact]
    public async Task ListDailyLedger_surfaces_an_unreadable_line_as_permanent_not_a_raw_parse_throw()
    {
        // ReadInt's GetValue<decimal> throws on a non-numeric account. Fail the page rather than
        // drop the line: creditor balances are summed from these debits and credits.
        var client = Make(new StubHandler(_ => Respond(
            HttpStatusCode.OK, """[{"entryNumber":1,"account":"not-a-number","debit":10.0}]""")));

        var act = async () => await client.ListDailyLedgerAsync(
            Instant.FromUnixTimeSeconds(0), Instant.FromUnixTimeSeconds(86400),
            Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HoldedPermanentException>();
    }

    [HumansFact]
    public async Task CreateExpenseAccount_posts_and_returns_id()
    {
        string? capturedMethod = null;
        var handler = new StubHandler(req =>
        {
            capturedMethod = req.Method.Method;
            return Respond(HttpStatusCode.OK, """{"id":"new1"}""");
        });

        var client = Make(handler);
        var id = await client.CreateExpenseAccountAsync(62900000, "Otros servicios", Xunit.TestContext.Current.CancellationToken);

        capturedMethod.Should().Be("POST");
        id.Should().Be("new1");
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
