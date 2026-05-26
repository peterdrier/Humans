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

public class HoldedClientContactTests
{
    private static HoldedClient Make(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.holded.com") },
            Options.Create(new HoldedClientOptions { ApiKey = "test-key" }),
            NullLogger<HoldedClient>.Instance);

    [HumansFact]
    public async Task GetContact_parses_supplierRecord_num()
    {
        var json = """{"id":"c1","name":"Daniela Real","supplierRecord":{"num":40000001}}""";
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var contact = await client.GetContactAsync("c1");

        contact.Id.Should().Be("c1");
        contact.SupplierAccountNum.Should().Be(40000001);
    }

    [HumansFact]
    public async Task GetContact_supplierAccountNum_null_when_absent()
    {
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, """{"id":"c1","name":"X"}""")));
        var contact = await client.GetContactAsync("c1");
        contact.SupplierAccountNum.Should().BeNull();
    }

    [HumansFact]
    public async Task UpsertContact_posts_when_no_existing_id_and_returns_id()
    {
        string? method = null;
        var client = Make(new StubHandler(req =>
        {
            method = req.Method.Method;
            return Respond(HttpStatusCode.OK, """{"id":"new-c"}""");
        }));

        var id = await client.UpsertContactAsync(new HoldedContactInput { Name = "Legal", CustomId = "u1" });

        method.Should().Be("POST");
        id.Should().Be("new-c");
    }

    [HumansFact]
    public async Task UpsertContact_puts_when_existing_id_present()
    {
        string? method = null;
        string? path = null;
        var client = Make(new StubHandler(req =>
        {
            method = req.Method.Method;
            path = req.RequestUri!.AbsolutePath;
            return Respond(HttpStatusCode.OK, """{"id":"c-exist"}""");
        }));

        var id = await client.UpsertContactAsync(new HoldedContactInput
        {
            Name = "Legal",
            TradeName = "Burner",
            ExistingContactId = "c-exist",
        });

        method.Should().Be("PUT");
        path.Should().EndWith("/contacts/c-exist");
        id.Should().Be("c-exist");
    }

    [HumansFact]
    public async Task ListChartOfAccounts_parses_num_name_balance()
    {
        var json = """[{"id":"a","num":40000001,"name":"Daniela","balance":-3180.0},{"id":"b","num":62900000,"name":"Otros","balance":12.0}]""";
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var rows = await client.ListChartOfAccountsAsync();

        rows.Should().HaveCount(2);
        rows.Single(r => r.Num == 40000001).Balance.Should().Be(-3180.0m);
    }

    [HumansFact]
    public async Task ListPayments_parses_contact_amount_date()
    {
        var json = """[{"id":"p1","contactId":"c1","amount":50.0,"date":1779141600,"documentType":"purchase"}]""";
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var rows = await client.ListPaymentsAsync();

        rows.Should().ContainSingle();
        rows[0].ContactId.Should().Be("c1");
        rows[0].Amount.Should().Be(50.0m);
        rows[0].Date.Should().Be(Instant.FromUnixTimeSeconds(1779141600));
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
