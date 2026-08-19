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

public class HoldedClientContactTests
{
    private static HoldedClient Make(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.holded.com") },
            Options.Create(new HoldedClientOptions { ApiKey = "test-key" }),
            NullLogger<HoldedClient>.Instance,
            new HoldedCallLog(),
            new FakeClock(Instant.FromUtc(2026, 8, 10, 12, 0)));

    [HumansFact]
    public async Task GetContact_parses_supplier_record_num()
    {
        var json = """{"id":"c1","name":"Daniela Real","supplier_record":{"num":40000001}}""";
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
    public async Task GetContact_parses_contact_info_fields_and_joins_bill_address()
    {
        var json = """
        {
          "id":"c1","name":"Daniela Real","trade_name":"Dani",
          "email":"dani@example.org","phone":"911111111","mobile":"600000000",
          "iban":"ES1234567890","code":"12345678A",
          "bill_address":{"address":"Calle Mayor 1","city":"Madrid","postal_code":"28001",
            "province":"Madrid","country":"Spain","country_code":"ES","info":null}
        }
        """;
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var contact = await client.GetContactAsync("c1", Xunit.TestContext.Current.CancellationToken);

        contact.TradeName.Should().Be("Dani");
        contact.Email.Should().Be("dani@example.org");
        contact.Phone.Should().Be("911111111");
        contact.Mobile.Should().Be("600000000");
        contact.Iban.Should().Be("ES1234567890");
        contact.TaxCode.Should().Be("12345678A");
        contact.Address.Should().Be("Calle Mayor 1, 28001, Madrid, Madrid, Spain");
    }

    [HumansFact]
    public async Task GetContact_contact_info_fields_null_when_bill_address_absent()
    {
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, """{"id":"c1","name":"X"}""")));

        var contact = await client.GetContactAsync("c1", Xunit.TestContext.Current.CancellationToken);

        contact.TradeName.Should().BeNull();
        contact.Address.Should().BeNull();
    }

    [HumansFact]
    public async Task ListContacts_parses_id_name_and_supplierAccountNum()
    {
        var json = """
        {"items":[
          {"id":"c1","name":"Daniela Real","supplier_record":{"num":40000001}},
          {"id":"c2","name":"Acme Supplies SL"}
        ],"cursor":null,"has_more":false}
        """;
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

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
        // node["supplier_record"]?["num"] indexer threw InvalidOperationException on it. The caller
        // degrades a throw to an empty list, so this one contact blanked every account name in prod.
        var json = """
        {"items":[
          {"id":"c1","name":"Client Only SL","supplier_record":[]},
          {"id":"c2","name":"Daniela Real","supplier_record":{"num":40000001}}
        ],"cursor":null,"has_more":false}
        """;
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var contacts = await client.ListContactsAsync(Xunit.TestContext.Current.CancellationToken);

        contacts.Should().HaveCount(2);
        contacts[0].Name.Should().Be("Client Only SL");
        contacts[0].SupplierAccountNum.Should().BeNull();
        contacts[1].SupplierAccountNum.Should().Be(40000001);
    }

    [HumansFact]
    public async Task ListContacts_surfaces_an_unreadable_page_envelope_as_permanent_not_a_raw_parse_throw()
    {
        // A page-level parse failure (the envelope, not one contact's value) is distinct from the
        // per-contact skip-and-log below (#994): the whole page is unreadable, so there is nothing
        // to skip-and-continue.
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, "not json")));

        var act = async () => await client.ListContactsAsync(Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HoldedPermanentException>();
    }

    [HumansFact]
    public async Task ListContacts_skips_an_unreadable_contact_and_keeps_the_rest()
    {
        // A value of the wrong type anywhere in the projection used to take down the whole page.
        var json = """
        {"items":[
          {"id":42,"name":"Numeric id"},
          {"id":"c2","name":"Daniela Real","supplier_record":{"num":40000001}}
        ],"cursor":null,"has_more":false}
        """;
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var contacts = await client.ListContactsAsync(Xunit.TestContext.Current.CancellationToken);

        contacts.Should().ContainSingle();
        contacts[0].Id.Should().Be("c2");
        contacts[0].SupplierAccountNum.Should().Be(40000001);
    }

    [HumansFact]
    public async Task GetContact_treats_a_non_object_supplierRecord_as_no_account()
    {
        var client = Make(new StubHandler(_ => Respond(
            HttpStatusCode.OK, """{"id":"c1","name":"X","supplier_record":[]}""")));

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
    public async Task ListContacts_follows_cursor_until_has_more_false()
    {
        // Regression for #976: an unpaged call silently truncated at whatever cap Holded applies.
        var callCount = 0;
        string? secondQuery = null;
        var handler = new StubHandler(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                return Respond(HttpStatusCode.OK,
                    """{"items":[{"id":"c1","name":"A"}],"cursor":"c1","has_more":true}""");
            }
            secondQuery = req.RequestUri!.Query;
            return Respond(HttpStatusCode.OK,
                """{"items":[{"id":"c2","name":"B"}],"cursor":null,"has_more":false}""");
        });
        var client = Make(handler);

        var contacts = await client.ListContactsAsync(Xunit.TestContext.Current.CancellationToken);

        contacts.Select(c => c.Id).Should().Equal("c1", "c2");
        callCount.Should().Be(2);
        secondQuery.Should().Contain("cursor=c1");
    }

    [HumansFact]
    public async Task ListContacts_stops_after_one_call_when_first_page_has_no_more()
    {
        var callCount = 0;
        var handler = new StubHandler(_ =>
        {
            callCount++;
            return Respond(HttpStatusCode.OK, """{"items":[],"cursor":null,"has_more":false}""");
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

    [HumansFact]
    public async Task UpsertContact_does_not_send_custom_id_v2_has_no_such_field()
    {
        // v2 contacts POST/PUT schema has no `custom_id` property (it is read-only on GET).
        string? capturedBody = null;
        var client = Make(new StubHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Respond(HttpStatusCode.OK, """{"id":"new-c"}""");
        }));

        await client.UpsertContactAsync(
            new HoldedContactInput { Name = "Legal", CustomId = "u1" },
            Xunit.TestContext.Current.CancellationToken);

        capturedBody.Should().NotContain("custom_id");
        capturedBody.Should().NotContain("customId");
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
