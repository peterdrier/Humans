using System.Net;
using System.Text;
using AwesomeAssertions;
using Humans.Application.Interfaces.Holded;
using Humans.Infrastructure.Services.Holded;
using Humans.Testing;
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
        var accounts = await client.ListExpenseAccountsAsync();

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
        var docs = await client.ListPurchaseDocumentsPageAsync(1, 10);

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
        await client.ListPurchaseDocumentsPageAsync(page: 3, limit: 50);

        capturedQuery.Should().Contain("page=3");
        capturedQuery.Should().Contain("limit=50");
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
        var id = await client.CreateExpenseAccountAsync(62900000, "Otros servicios");

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
