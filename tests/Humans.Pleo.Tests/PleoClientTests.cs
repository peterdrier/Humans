using System.Net;
using System.Text;
using AwesomeAssertions;
using Humans.Pleo.Services;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Pleo.Tests;

public sealed class PleoClientTests
{
    [HumansTheory]
    [Xunit.InlineData("not a URI", false)]
    [Xunit.InlineData("", false)]
    [Xunit.InlineData("/relative", false)]
    [Xunit.InlineData("ftp://pleo.example", false)]
    [Xunit.InlineData("https://pleo.example", true)]
    [Xunit.InlineData("http://pleo.example", true)]
    public async Task Register_Endpoint_ResolvesBeforeValidation(string baseUrl, bool valid)
    {
        var services = new ServiceCollection();
        new Section().Register(services, new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Pleo:BaseUrl"] = baseUrl }).Build());
        services.Configure<PleoClientOptions>(o => { o.ApiKey = "test-key"; o.CompanyId = "company-1"; });
        var calls = 0;
        services.AddHttpClient<IPleoClient, PleoClient>().ConfigurePrimaryHttpMessageHandler(() => new StubHandler(request =>
        {
            calls++;
            request.RequestUri!.Host.Should().Be("pleo.example");
            return Json(Page("", false, null));
        }));
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IPleoClient>();
        var read = () => client.SearchEntriesAsync(Xunit.TestContext.Current.CancellationToken);

        client.IsConfigured.Should().BeTrue();
        if (valid)
            (await read()).Should().BeEmpty();
        else
            await read.Should().ThrowAsync<PleoPermanentException>().WithMessage("*absolute HTTP(S)*");
        calls.Should().Be(valid ? 1 : 0);
    }

    private static PleoClient Make(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("https://external.pleo.io") },
            Options.Create(new PleoClientOptions { ApiKey = "test-key", CompanyId = "company-1" }));

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Page(string ids, bool hasNext, string? cursor)
    {
        var next = hasNext ? "true" : "false";
        var end = cursor is null ? "null" : $"\"{cursor}\"";
        return $$"""{"data":[{{ids}}],"pagination":{"hasNextPage":{{next}},"endCursor":{{end}} } }""";
    }

    [HumansFact]
    public async Task Entries_pager_follows_endCursor_across_pages()
    {
        var urls = new List<string>();
        var client = Make(req =>
        {
            urls.Add(req.RequestUri!.PathAndQuery);
            return urls.Count switch
            {
                1 => Json(Page("""{"id":"a"},{"id":"b"}""", true, "c1=")),
                2 => Json(Page("""{"id":"c"}""", true, "c2")),
                _ => Json(Page("""{"id":"d"}""", false, "c3")),
            };
        });

        var items = await client.SearchEntriesAsync(Xunit.TestContext.Current.CancellationToken);

        items.Select(i => i.GetProperty("id").GetString()).Should().Equal("a", "b", "c", "d");
        urls.Should().HaveCount(3);
        urls[0].Should().Be("/v1/accounting-entries:search?company_id=company-1&limit=100");
        urls[1].Should().EndWith("&after=c1%3D");
        urls[2].Should().EndWith("&after=c2");
    }

    [HumansFact]
    public async Task Employee_history_includes_deleted_cardholders_across_pages()
    {
        const string active = """{"id":"active","firstName":"Ana","lastName":"Active","isDeleted":false}""";
        const string deleted = """{"id":"deleted","firstName":"Dee","lastName":"Departed","isDeleted":true}""";
        var client = Make(req =>
        {
            var query = QueryHelpers.ParseQuery(req.RequestUri!.Query);
            if (!string.Equals(query["includeDeleted"], "true", StringComparison.Ordinal))
                return Json(Page(active, false, null));
            return query.ContainsKey("after")
                ? Json(Page(deleted, false, null))
                : Json(Page(active, true, "next"));
        });

        var employees = await client.ListEmployeesAsync(Xunit.TestContext.Current.CancellationToken);

        employees.Select(e => e.Id).Should().Equal("active", "deleted");
        var former = employees.Single(e => e.IsDeleted);
        former.FirstName.Should().Be("Dee");
        former.LastName.Should().Be("Departed");
    }

    [HumansFact]
    public async Task Pager_fails_closed_on_a_repeated_cursor()
    {
        var client = Make(_ => Json(Page("""{"id":"a"}""", true, "same")));

        var act = () => client.SearchEntriesAsync(Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<PleoTransientException>().WithMessage("*repeated cursor*");
    }

    [HumansFact]
    public async Task Pager_fails_closed_on_a_body_without_data()
    {
        var client = Make(_ => Json("""{"error":"nope"}"""));

        var act = () => client.SearchEntriesAsync(Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<PleoTransientException>().WithMessage("*without a data array*");
    }

    [HumansTheory]
    [Xunit.InlineData("entries", null)]
    [Xunit.InlineData("accounts", "{}")]
    [Xunit.InlineData("employees", "null")]
    [Xunit.InlineData("receipts", """{"hasNextPage":null}""")]
    [Xunit.InlineData("entries", """{"hasNextPage":"false"}""")]
    public async Task Pager_rejects_unknown_completion_instead_of_returning_a_partial_dataset(string consumer, string? pagination)
    {
        var calls = 0;
        var client = Make(_ => ++calls == 1
            ? Json(Page("""{"id":"a"}""", true, "next"))
            : Json(pagination is null ? """{"data":[]}""" : $$"""{"data":[],"pagination":{{pagination}}}"""));

        var ct = Xunit.TestContext.Current.CancellationToken;
        Func<Task> action = () => consumer switch
        {
            "accounts" => client.SearchAccountsAsync(ct),
            "employees" => client.ListEmployeesAsync(ct),
            "receipts" => client.ListReceiptsAsync("entry", ct),
            _ => client.SearchEntriesAsync(ct),
        };

        await action.Should().ThrowAsync<PleoTransientException>().WithMessage("*hasNextPage*");
        calls.Should().Be(2);
    }

    [HumansTheory]
    [Xunit.InlineData("entries", "null")]
    [Xunit.InlineData("accounts", "[]")]
    [Xunit.InlineData("employees", "42")]
    [Xunit.InlineData("balance", "true")]
    [Xunit.InlineData("receipts", "\"unavailable\"")]
    public async Task Non_object_responses_use_the_typed_provider_error_path(string consumer, string body)
    {
        var client = Make(_ => Json(body));
        var ct = Xunit.TestContext.Current.CancellationToken;
        Func<Task> action = () => consumer switch
        {
            "accounts" => client.SearchAccountsAsync(ct),
            "employees" => client.ListEmployeesAsync(ct),
            "balance" => client.GetBalanceAsync(ct),
            "receipts" => client.ListReceiptsAsync("entry", ct),
            _ => client.SearchEntriesAsync(ct),
        };

        await action.Should().ThrowAsync<PleoPermanentException>().WithMessage("*expected a JSON object*");
    }

    [HumansTheory]
    [Xunit.InlineData(PleoTypedRead.Accounts, "{}")]
    [Xunit.InlineData(PleoTypedRead.Accounts, "{\"id\":null}")]
    [Xunit.InlineData(PleoTypedRead.Categories, "{}")]
    [Xunit.InlineData(PleoTypedRead.Categories, "{\"id\":\"\"}")]
    [Xunit.InlineData(PleoTypedRead.Employees, "{}")]
    [Xunit.InlineData(PleoTypedRead.Employees, "{\"id\":\" \"}")]
    [Xunit.InlineData(PleoTypedRead.Receipts, "{}")]
    [Xunit.InlineData(PleoTypedRead.Receipts, "{\"id\":\"\"}")]
    public async Task Typed_reads_reject_items_without_usable_identities(PleoTypedRead consumer, string item)
    {
        var client = Make(_ => Json(Page(item, false, null)));
        var ct = Xunit.TestContext.Current.CancellationToken;
        Func<Task> action = () => consumer switch
        {
            PleoTypedRead.Accounts => client.SearchAccountsAsync(ct),
            PleoTypedRead.Categories => client.SearchCategoriesAsync(ct),
            PleoTypedRead.Employees => client.ListEmployeesAsync(ct),
            _ => client.ListReceiptsAsync("entry", ct),
        };

        await action.Should().ThrowAsync<PleoPermanentException>();
    }

    [HumansTheory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("")]
    [Xunit.InlineData(" ")]
    public async Task Balance_rejects_a_missing_currency_instead_of_replacing_the_reading(string? currency)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            data = new
            {
                accounts = new[] { new
                {
                    isPrimary = true,
                    balance = new
                    {
                        current = new { currency, value = 0 },
                        available = new { value = 0 },
                        pendingDebits = new { value = 0 },
                    },
                } },
            },
        });
        var client = Make(_ => Json(body));

        var act = () => client.GetBalanceAsync(Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<PleoPermanentException>();
    }

    [HumansFact]
    public async Task Balance_preserves_explicit_zero_and_a_foreign_currency()
    {
        var client = Make(_ => Json("""
            {"data":{"accounts":[{"isPrimary":true,"balance":{
              "current":{"currency":"USD","value":0},"available":{"value":0},"pendingDebits":{"value":0}
            }}]}}
            """));

        var balance = await client.GetBalanceAsync(Xunit.TestContext.Current.CancellationToken);

        balance.Should().Be(new PleoBalanceWire("USD", 0, 0, 0));
    }

    [HumansFact]
    public async Task Sends_basic_auth_with_the_key_and_an_empty_password()
    {
        HttpRequestMessage? captured = null;
        var client = Make(req =>
        {
            captured = req;
            return Json(Page("", false, null));
        });

        await client.ListEmployeesAsync(Xunit.TestContext.Current.CancellationToken);

        captured!.Method.Should().Be(HttpMethod.Get);
        captured.Headers.Authorization!.Scheme.Should().Be("Basic");
        Encoding.UTF8.GetString(Convert.FromBase64String(captured.Headers.Authorization.Parameter!))
            .Should().Be("test-key:");
    }

    [HumansTheory]
    [Xunit.InlineData(298, "😀")]
    [Xunit.InlineData(299, "")]
    public async Task Permanent_error_body_preview_preserves_unicode(int prefixLength, string suffix)
    {
        var prefix = new string('e', prefixLength);
        var client = Make(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(prefix + "😀tail", Encoding.UTF8),
        });

        var act = () => client.GetBalanceAsync(Xunit.TestContext.Current.CancellationToken);
        var failure = (await act.Should().ThrowAsync<PleoPermanentException>()).Which;

        failure.StatusCode.Should().Be(403);
        var encode = () => new UTF8Encoding(false, true).GetBytes(failure.Message);
        encode.Should().NotThrow();
        failure.Message.Should().EndWith(prefix + suffix);
    }

    [HumansFact]
    public async Task A_403_is_permanent_and_a_503_transient()
    {
        var forbidden = Make(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("RBAC") });
        var down = Make(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var ct = Xunit.TestContext.Current.CancellationToken;

        (await forbidden.Invoking(c => c.GetBalanceAsync(ct)).Should().ThrowAsync<PleoPermanentException>())
            .Which.StatusCode.Should().Be(403);
        await down.Invoking(c => c.GetBalanceAsync(ct)).Should().ThrowAsync<PleoTransientException>();
    }

    [HumansFact]
    public async Task Receipt_file_is_downloaded_from_the_signed_url_without_the_key()
    {
        var requests = new List<HttpRequestMessage>();
        var client = Make(req =>
        {
            requests.Add(req);
            if (string.Equals(req.RequestUri!.Host, "external.pleo.io", StringComparison.Ordinal))
                return Json("""{"data":[{"id":"r1","mimeType":"application/pdf","url":"https://receipts.cdn.pleo.io/x.pdf?sig=1"}],"pagination":{"hasNextPage":false}}""");
            var file = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0x25, 0x50]) };
            file.Content.Headers.ContentType = new("application/pdf");
            return file;
        });

        var receipt = await client.GetReceiptFileAsync("entry-1", "r1", Xunit.TestContext.Current.CancellationToken);

        receipt.ContentType.Should().Be("application/pdf");
        receipt.Content.Should().Equal(0x25, 0x50);
        requests[1].RequestUri!.Host.Should().Be("receipts.cdn.pleo.io");
        requests[1].Headers.Authorization.Should().BeNull();
    }

    [HumansFact]
    public async Task Receipt_url_off_pleo_is_refused()
    {
        var client = Make(_ =>
            Json("""{"data":[{"id":"r1","url":"https://evil.example/x"}],"pagination":{"hasNextPage":false}}"""));

        var act = () => client.GetReceiptFileAsync("entry-1", "r1", Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<PleoPermanentException>();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}

public enum PleoTypedRead { Accounts, Categories, Employees, Receipts }
