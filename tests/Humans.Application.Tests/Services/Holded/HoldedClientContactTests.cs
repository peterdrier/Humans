using System.Net;
using System.Text;
using AwesomeAssertions;
using Humans.Application.Interfaces.Holded;
using Humans.Infrastructure.Services.Holded;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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

        var contact = await client.GetContactAsync("c1", Xunit.TestContext.Current.CancellationToken);

        contact.Id.Should().Be("c1");
        contact.SupplierAccountNum.Should().Be(40000001);
    }

    [HumansFact]
    public async Task GetContact_supplierAccountNum_null_when_absent()
    {
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, """{"id":"c1","name":"X"}""")));
        var contact = await client.GetContactAsync("c1", Xunit.TestContext.Current.CancellationToken);
        contact.SupplierAccountNum.Should().BeNull();
    }

    [HumansFact]
    public async Task ListContacts_parses_id_name_and_supplierAccountNum()
    {
        var json = """
        [
          {"id":"c1","name":"Daniela Real","supplierRecord":{"num":40000001}},
          {"id":"c2","name":"Acme Supplies SL"}
        ]
        """;
        var callCount = 0;
        var client = Make(new StubHandler(_ =>
            Respond(HttpStatusCode.OK, callCount++ == 0 ? json : "[]")));

        var contacts = await client.ListContactsAsync(Xunit.TestContext.Current.CancellationToken);

        contacts.Should().HaveCount(2);
        contacts[0].Id.Should().Be("c1");
        contacts[0].SupplierAccountNum.Should().Be(40000001);
        contacts[1].Id.Should().Be("c2");
        contacts[1].SupplierAccountNum.Should().BeNull();
    }

    [HumansFact]
    public async Task ListContacts_treats_a_non_object_supplierRecord_as_no_account()
    {
        // Regression for #994: Holded sends an absent sub-record as an empty array, and the raw
        // node["supplierRecord"]?["num"] indexer threw InvalidOperationException on it. The caller
        // degrades a throw to an empty list, so this one contact blanked every account name in prod.
        var json = """
        [
          {"id":"c1","name":"Client Only SL","supplierRecord":[]},
          {"id":"c2","name":"Daniela Real","supplierRecord":{"num":40000001}}
        ]
        """;
        var callCount = 0;
        var client = Make(new StubHandler(_ =>
            Respond(HttpStatusCode.OK, callCount++ == 0 ? json : "[]")));

        var contacts = await client.ListContactsAsync(Xunit.TestContext.Current.CancellationToken);

        contacts.Should().HaveCount(2);
        contacts[0].Name.Should().Be("Client Only SL");
        contacts[0].SupplierAccountNum.Should().BeNull();
        contacts[1].SupplierAccountNum.Should().Be(40000001);
    }

    [HumansFact]
    public async Task ListContacts_skips_an_unreadable_contact_and_keeps_the_rest()
    {
        // A value of the wrong type anywhere in the projection used to take down the whole page.
        var json = """
        [
          {"id":42,"name":"Numeric id"},
          {"id":"c2","name":"Daniela Real","supplierRecord":{"num":40000001}}
        ]
        """;
        var callCount = 0;
        var client = Make(new StubHandler(_ =>
            Respond(HttpStatusCode.OK, callCount++ == 0 ? json : "[]")));

        var contacts = await client.ListContactsAsync(Xunit.TestContext.Current.CancellationToken);

        contacts.Should().ContainSingle();
        contacts[0].Id.Should().Be("c2");
        contacts[0].SupplierAccountNum.Should().Be(40000001);
    }

    [HumansFact]
    public async Task GetContact_treats_a_non_object_supplierRecord_as_no_account()
    {
        var client = Make(new StubHandler(_ => Respond(
            HttpStatusCode.OK, """{"id":"c1","name":"X","supplierRecord":[]}""")));

        var contact = await client.GetContactAsync("c1", Xunit.TestContext.Current.CancellationToken);

        contact.Name.Should().Be("X");
        contact.SupplierAccountNum.Should().BeNull();
    }

    [HumansFact]
    public async Task GetContact_surfaces_an_unreadable_contact_as_permanent_not_a_raw_parse_throw()
    {
        // ProcessHoldedCreateAsync catches only the client's typed exceptions, and so does the outbox
        // drain loop above it — a raw InvalidOperationException here would abort the whole batch and
        // leave its own event neither processed nor failed.
        var client = Make(new StubHandler(_ => Respond(
            HttpStatusCode.OK, """{"id":42,"name":"Numeric id"}""")));

        var act = async () => await client.GetContactAsync("c1", Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HoldedPermanentException>();
    }

    [HumansFact]
    public async Task ListContacts_walks_pages_until_an_empty_page_returns()
    {
        // Regression for #976: an unpaged call silently truncated at whatever cap Holded applies.
        var capturedPages = new List<string>();
        var handler = new StubHandler(req =>
        {
            capturedPages.Add(req.RequestUri!.Query);
            var page = req.RequestUri.Query.Contains("page=1", StringComparison.Ordinal) ? 1
                : req.RequestUri.Query.Contains("page=2", StringComparison.Ordinal) ? 2
                : 3;
            return page switch
            {
                1 => Respond(HttpStatusCode.OK, """[{"id":"c1","name":"A"}]"""),
                2 => Respond(HttpStatusCode.OK, """[{"id":"c2","name":"B"}]"""),
                _ => Respond(HttpStatusCode.OK, "[]"),
            };
        });
        var client = Make(handler);

        var contacts = await client.ListContactsAsync(Xunit.TestContext.Current.CancellationToken);

        contacts.Select(c => c.Id).Should().Equal("c1", "c2");
        capturedPages.Should().HaveCount(3);
        capturedPages[0].Should().Contain("page=1");
        capturedPages[1].Should().Contain("page=2");
        capturedPages[2].Should().Contain("page=3");
    }

    [HumansFact]
    public async Task ListContacts_stops_after_one_call_when_first_page_is_empty()
    {
        var callCount = 0;
        var handler = new StubHandler(_ =>
        {
            callCount++;
            return Respond(HttpStatusCode.OK, "[]");
        });
        var client = Make(handler);

        var contacts = await client.ListContactsAsync(Xunit.TestContext.Current.CancellationToken);

        contacts.Should().BeEmpty();
        callCount.Should().Be(1);
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

        var id = await client.UpsertContactAsync(new HoldedContactInput { Name = "Legal", CustomId = "u1" }, Xunit.TestContext.Current.CancellationToken);

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
        }, Xunit.TestContext.Current.CancellationToken);

        method.Should().Be("PUT");
        path.Should().EndWith("/contacts/c-exist");
        id.Should().Be("c-exist");
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
