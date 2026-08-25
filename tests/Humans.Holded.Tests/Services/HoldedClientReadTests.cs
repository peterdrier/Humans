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

public class HoldedClientReadTests
{
    private static HoldedClient Make(StubHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.holded.com") },
            Options.Create(new HoldedClientOptions { ApiKey = "test-key" }),
            NullLogger<HoldedClient>.Instance,
            new HoldedCallLog(),
            new FakeClock(Instant.FromUtc(2026, 8, 10, 12, 0)));

    [HumansFact]
    public async Task ListExpenseAccounts_parses_num_and_name()
    {
        var json = """{"items":[{"id":"a1","name":"Otros servicios","account_num":62900000,"archived":false}],"cursor":null,"has_more":false}""";
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, json));

        var client = Make(handler);
        var accounts = await client.ListExpenseAccountsAsync(Xunit.TestContext.Current.CancellationToken);

        accounts.Should().HaveCount(1);
        accounts[0].Id.Should().Be("a1");
        accounts[0].Name.Should().Be("Otros servicios");
        accounts[0].AccountNum.Should().Be(62900000);
    }

    [HumansFact]
    public async Task ListExpenseAccounts_surfaces_an_unreadable_account_as_permanent_not_a_raw_parse_throw()
    {
        var json = """{"items":[{"id":42,"name":"Otros servicios","account_num":62900000}],"cursor":null,"has_more":false}""";
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var act = async () => await client.ListExpenseAccountsAsync(Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HoldedPermanentException>();
    }

    [HumansFact]
    public async Task ListPurchaseDocuments_parses_lines_account_and_tags()
    {
        var json = """
        {"items":[
          {
            "id":"doc-1","document_number":"F001","contact_name":"Alice",
            "date":"2026-05-14",
            "subtotal":"100.00","tax":"21.00","total":"121.00",
            "currency":"eur","tags":["adminstaff"],
            "lines":[
              {"price":"100.00","account":"acc-629","tags":["adminstaff"]}
            ]
          }
        ],"cursor":null,"has_more":false}
        """;
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, json));

        var client = Make(handler);
        var docs = await client.ListPurchaseDocumentsAsync(Xunit.TestContext.Current.CancellationToken);

        docs.Should().HaveCount(1);
        var doc = docs[0];
        doc.Id.Should().Be("doc-1");
        doc.Date.Should().Be(
            new LocalDate(2026, 5, 14)
                .AtStartOfDayInZone(DateTimeZoneProviders.Tzdb["Europe/Madrid"])
                .ToInstant());
        doc.Tags.Should().ContainSingle("adminstaff");

        doc.Lines.Should().HaveCount(1);
        var line = doc.Lines[0];
        line.AccountId.Should().Be("acc-629");
        line.Tags.Should().ContainSingle("adminstaff");
        line.Amount.Should().Be(100.0m);
    }

    [HumansFact]
    public async Task ListPurchaseDocuments_puts_limit_in_query_string()
    {
        string? capturedQuery = null;
        var handler = new StubHandler(req =>
        {
            capturedQuery = req.RequestUri!.Query;
            return Respond(HttpStatusCode.OK, """{"items":[],"cursor":null,"has_more":false}""");
        });

        var client = Make(handler);
        await client.ListPurchaseDocumentsAsync(Xunit.TestContext.Current.CancellationToken);

        capturedQuery.Should().Contain("limit=200");
    }

    [HumansFact]
    public async Task ListPurchaseDocuments_treats_a_non_array_lines_as_no_lines()
    {
        // Holded sends an absent sub-record as an empty array (#994) and is equally free to send an
        // absent collection as something other than an array; AsArray() throws on both.
        var json = """{"items":[{"id":"doc-1","document_number":"F001","date":"2026-05-14","total":"121.00","lines":{},"tags":""}],"cursor":null,"has_more":false}""";
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var docs = await client.ListPurchaseDocumentsAsync(Xunit.TestContext.Current.CancellationToken);

        docs.Should().ContainSingle();
        docs[0].Lines.Should().BeEmpty();
        docs[0].Tags.Should().BeEmpty();
    }

    [HumansFact]
    public async Task ListPurchaseDocuments_surfaces_an_unreadable_doc_as_permanent_not_a_raw_parse_throw()
    {
        // Callers handle only IHoldedClient's two typed exceptions; a raw parse throw from a bad
        // stored value would escape the client and leak an Infrastructure detail upward.
        var client = Make(new StubHandler(_ => Respond(
            HttpStatusCode.OK, """{"items":[{"id":42,"document_number":"F001"}],"cursor":null,"has_more":false}""")));

        var act = async () => await client.ListPurchaseDocumentsAsync(Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HoldedPermanentException>();
    }

    [HumansFact]
    public async Task ListPurchaseDocuments_parses_the_draft_flag()
    {
        // Neither ?approval_status=draft nor ?draft=true reliably filters the live list endpoint
        // (verified live, 2026-08-25), so approval state is read from each item's own `draft`
        // field in the one full pull instead of a second sweep.
        var json = """
        {"items":[
          {"id":"d1","document_number":"F001","date":"2026-05-14","total":"121.00","draft":false},
          {"id":"d2","document_number":"F002","date":"2026-05-14","total":"50.00","draft":true},
          {"id":"d3","document_number":"F003","date":"2026-05-14","total":"30.00"}
        ],"cursor":null,"has_more":false}
        """;
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var docs = await client.ListPurchaseDocumentsAsync(Xunit.TestContext.Current.CancellationToken);

        docs.Single(d => string.Equals(d.Id, "d1", StringComparison.Ordinal)).IsDraft.Should().Be(false);
        docs.Single(d => string.Equals(d.Id, "d2", StringComparison.Ordinal)).IsDraft.Should().Be(true);
        docs.Single(d => string.Equals(d.Id, "d3", StringComparison.Ordinal)).IsDraft.Should().BeNull();
    }

    [HumansFact]
    public async Task ListLedgerEntries_parses_ddMMyyyy_dates_and_string_decimals()
    {
        var json = """
        {"items":[{"entry_number":2064,"line":2,"date":"09/02/2026","type":"payment",
          "description":"","doc_description":"","account":40000004,"debit":"0.00",
          "credit":"1200.00","tags":[],"checked":false}],"cursor":null,"has_more":false}
        """;
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var lines = await client.ListLedgerEntriesAsync(
            new LocalDate(2026, 1, 1), new LocalDate(2026, 12, 31),
            ct: Xunit.TestContext.Current.CancellationToken);

        lines.Should().HaveCount(1);
        var line = lines[0];
        line.EntryNumber.Should().Be(2064);
        line.Line.Should().Be(2);
        line.AccountNum.Should().Be(40000004);
        line.Debit.Should().Be(0.00m);
        line.Credit.Should().Be(1200.00m);
        line.Type.Should().Be("payment");
        line.Date.Should().Be(
            new LocalDate(2026, 2, 9)
                .AtStartOfDayInZone(DateTimeZoneProviders.Tzdb["Europe/Madrid"])
                .ToInstant());
    }

    [HumansFact]
    public async Task ListLedgerEntries_follows_cursor_until_has_more_false()
    {
        var callCount = 0;
        string? secondQuery = null;
        var handler = new StubHandler(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                return Respond(HttpStatusCode.OK, """
                {"items":[{"entry_number":1,"line":1,"date":"01/01/2026","account":40000004,
                  "debit":"0.00","credit":"10.00"}],"cursor":"c1","has_more":true}
                """);
            }
            secondQuery = req.RequestUri!.Query;
            return Respond(HttpStatusCode.OK, """
            {"items":[{"entry_number":2,"line":1,"date":"02/01/2026","account":40000004,
              "debit":"0.00","credit":"5.00"}],"cursor":null,"has_more":false}
            """);
        });

        var client = Make(handler);
        var lines = await client.ListLedgerEntriesAsync(
            new LocalDate(2026, 1, 1), new LocalDate(2026, 1, 31),
            ct: Xunit.TestContext.Current.CancellationToken);

        lines.Should().HaveCount(2);
        callCount.Should().Be(2);
        secondQuery.Should().Contain("cursor=c1");
    }

    [HumansFact]
    public async Task ListLedgerEntries_throws_when_page_cap_hit()
    {
        // A truncated fetch here would drive replace-semantics reconciliation to delete rows that
        // still exist in Holded — lossy becomes destructive, so the cap must throw, never log-and-return.
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, """
            {"items":[],"cursor":"c1","has_more":true}
            """));

        var client = Make(handler);
        var act = async () => await client.ListLedgerEntriesAsync(
            new LocalDate(2026, 1, 1), new LocalDate(2026, 1, 31),
            ct: Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HoldedTransientException>();
    }

    [HumansFact]
    public async Task ListLedgerEntries_passes_account_filter()
    {
        string? capturedQuery = null;
        var handler = new StubHandler(req =>
        {
            capturedQuery = req.RequestUri!.Query;
            return Respond(HttpStatusCode.OK, """{"items":[],"cursor":null,"has_more":false}""");
        });

        var client = Make(handler);
        await client.ListLedgerEntriesAsync(
            new LocalDate(2026, 1, 1), new LocalDate(2026, 1, 31), accountNum: 40000004,
            ct: Xunit.TestContext.Current.CancellationToken);

        capturedQuery.Should().Contain("account=40000004");
    }

    [HumansFact]
    public async Task ListLedgerEntries_sends_end_date_one_day_past_the_inclusive_to()
    {
        // end_date is exclusive on the live API (probed 2026-08-25: an entry dated 25/08/2026 is
        // absent with end_date=2026-08-25, present with end_date=2026-08-26), so the request must
        // carry to+1 day for this method's own `to` parameter to stay inclusive.
        string? capturedQuery = null;
        var handler = new StubHandler(req =>
        {
            capturedQuery = req.RequestUri!.Query;
            return Respond(HttpStatusCode.OK, """{"items":[],"cursor":null,"has_more":false}""");
        });

        var client = Make(handler);
        await client.ListLedgerEntriesAsync(
            new LocalDate(2026, 8, 20), new LocalDate(2026, 8, 25),
            ct: Xunit.TestContext.Current.CancellationToken);

        capturedQuery.Should().Contain("end_date=2026-08-26");
    }

    [HumansFact]
    public async Task ListLedgerEntries_surfaces_an_unreadable_line_as_permanent_not_a_raw_parse_throw()
    {
        // Fail the page rather than drop the line: creditor and account balances are summed from
        // these debits and credits, and a quietly dropped line reads as a settled entry that never happened.
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK,
            """{"items":[{"entry_number":1,"account":"not-a-number","debit":"10.00"}],"cursor":null,"has_more":false}""")));

        var act = async () => await client.ListLedgerEntriesAsync(
            new LocalDate(2026, 1, 1), new LocalDate(2026, 1, 31),
            ct: Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HoldedPermanentException>();
    }

    [HumansFact]
    public async Task ListAccountingAccounts_parses_totals()
    {
        var json = """
        {"items":[{"id":"a1","color":"#fff","number":10000000,"name":"Capital",
          "description":"","group":"Equity","debit":"0.00","credit":"1000.00",
          "balance":"-1000.00","archived":false,"non_deductible":false}],
         "cursor":null,"has_more":false}
        """;
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var accounts = await client.ListAccountingAccountsAsync(Xunit.TestContext.Current.CancellationToken);

        accounts.Should().HaveCount(1);
        var account = accounts[0];
        account.Id.Should().Be("a1");
        account.Number.Should().Be(10000000);
        account.Name.Should().Be("Capital");
        account.Group.Should().Be("Equity");
        account.Debit.Should().Be(0.00m);
        account.Credit.Should().Be(1000.00m);
        account.Balance.Should().Be(-1000.00m);
        account.Archived.Should().BeFalse();
    }

    [HumansFact]
    public async Task GetUsage_parses_period_usage_limit_and_secondary()
    {
        var json = """
        {"type":"automation_token","period":"2026-08","usage":36,"limit":2000000,
         "count":1,"secondary_usages":{"api_v1_legacy_1":35},"user_usages":[],
         "next_plan":null,"next_limit":null}
        """;
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var usage = await client.GetUsageAsync(Xunit.TestContext.Current.CancellationToken);

        usage.Period.Should().Be("2026-08");
        usage.Usage.Should().Be(36);
        usage.Limit.Should().Be(2000000);
        usage.SecondaryUsages.Should().ContainKey("api_v1_legacy_1").WhoseValue.Should().Be(35);
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

    [HumansFact]
    public async Task CreateExpenseAccount_surfaces_an_unreadable_response_as_permanent_not_a_raw_parse_throw()
    {
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, "not json")));

        var act = async () => await client.CreateExpenseAccountAsync(
            62900000, "Otros servicios", Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HoldedPermanentException>();
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
