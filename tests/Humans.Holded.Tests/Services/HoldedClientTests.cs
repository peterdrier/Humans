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

public class HoldedClientTests
{
    private static HoldedClient Make(StubHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.holded.com") },
            Options.Create(new HoldedClientOptions { ApiKey = "test-key" }),
            NullLogger<HoldedClient>.Instance,
            new HoldedCallLog(),
            new FakeClock(Instant.FromUtc(2026, 8, 10, 12, 0)));

    [HumansTheory]
    [Xunit.InlineData("expense-account", "{malformed")]
    [Xunit.InlineData("expense-account", "{\"id\":42}")]
    [Xunit.InlineData("expense-account", "null", false)]
    [Xunit.InlineData("expense-account", "{}", false)]
    [Xunit.InlineData("expense-account", "{\"id\":null}", false)]
    [Xunit.InlineData("expense-account", "{\"id\":\"\"}", false)]
    [Xunit.InlineData("expense-account", "{\"id\":\"  \"}", false)]
    [Xunit.InlineData("expense-account", "{\"id\":\"account-1\"}", true, true)]
    [Xunit.InlineData("purchase", "{\"id\":\"doc-1\"}", true, true)]
    [Xunit.InlineData("contact", "{\"id\":\"contact-1\"}", true, true)]
    [Xunit.InlineData("invoice", "{\"id\":\"invoice-1\"}", true, true)]
    [Xunit.InlineData("receipt", "{\"id\":\"receipt-1\"}", true, true)]
    [Xunit.InlineData("purchase", "{malformed")]
    [Xunit.InlineData("purchase", "{\"id\":42}")]
    [Xunit.InlineData("contact", "{malformed")]
    [Xunit.InlineData("contact", "{\"id\":42}")]
    [Xunit.InlineData("invoice", "{malformed")]
    [Xunit.InlineData("invoice", "{\"id\":42}")]
    [Xunit.InlineData("receipt", "{malformed")]
    [Xunit.InlineData("receipt", "{\"id\":42}")]
    [Xunit.InlineData("purchase", "null", false)]
    [Xunit.InlineData("purchase", "{}", false)]
    [Xunit.InlineData("purchase", "{\"id\":null}", false)]
    [Xunit.InlineData("purchase", "{\"id\":\"\"}", false)]
    [Xunit.InlineData("purchase", "{\"id\":\"  \"}", false)]
    [Xunit.InlineData("contact", "null", false)]
    [Xunit.InlineData("contact", "{}", false)]
    [Xunit.InlineData("contact", "{\"id\":null}", false)]
    [Xunit.InlineData("contact", "{\"id\":\"\"}", false)]
    [Xunit.InlineData("contact", "{\"id\":\"  \"}", false)]
    [Xunit.InlineData("invoice", "null", false)]
    [Xunit.InlineData("invoice", "{}", false)]
    [Xunit.InlineData("invoice", "{\"id\":null}", false)]
    [Xunit.InlineData("invoice", "{\"id\":\"\"}", false)]
    [Xunit.InlineData("invoice", "{\"id\":\"  \"}", false)]
    [Xunit.InlineData("receipt", "null", false)]
    [Xunit.InlineData("receipt", "{}", false)]
    [Xunit.InlineData("receipt", "{\"id\":null}", false)]
    [Xunit.InlineData("receipt", "{\"id\":\"\"}", false)]
    [Xunit.InlineData("receipt", "{\"id\":\"  \"}", false)]
    public async Task CreationResponses_normalize_unreadable_success_without_retrying(string operation, string json, bool parseError = true, bool invalidCharset = false)
    {
        var calls = 0;
        var client = Make(new StubHandler(_ =>
        {
            calls++;
            var response = Respond(HttpStatusCode.Created, json);
            if (invalidCharset) response.Content.Headers.ContentType!.CharSet = "unsupported-charset";
            return response;
        }));
        var date = Instant.FromUtc(2026, 5, 10, 0, 0);
        Func<Task<string>> act = operation switch
        {
            "expense-account" => () => client.CreateExpenseAccountAsync(62900000, "Otros servicios",
                Xunit.TestContext.Current.CancellationToken),
            "purchase" => () => client.CreatePurchaseDocumentAsync(new()
            { ContactId = "contact-1", ContactName = "Alice", Date = date, Lines = [] },
                Xunit.TestContext.Current.CancellationToken),
            "contact" => () => client.UpsertContactAsync(new() { Name = "Alice" },
                Xunit.TestContext.Current.CancellationToken),
            _ => () => client.CreateSalesDocumentAsync(
                string.Equals(operation, "invoice", StringComparison.Ordinal)
                    ? HoldedSalesDocumentKind.Invoice : HoldedSalesDocumentKind.SalesReceipt,
                new() { ContactId = "contact-1", Date = date, Lines = [] },
                Xunit.TestContext.Current.CancellationToken),
        };

        var failure = await act.Should().ThrowAsync<HoldedPermanentException>();
        if (parseError) failure.Which.InnerException.Should().NotBeNull();
        calls.Should().Be(1);
    }

    [HumansFact]
    public async Task CreatePurchaseDocumentAsync_PostsExpectedJson_AndReturnsId()
    {
        string? capturedBody = null;
        var handler = new StubHandler(req =>
        {
            req.Method.Method.Should().Be("POST");
            req.RequestUri!.PathAndQuery.Should().Be("/api/v2/purchases");
            req.Headers.Authorization!.Scheme.Should().Be("Bearer");
            req.Headers.Authorization!.Parameter.Should().Be("test-key");
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Respond(HttpStatusCode.Created, """{"id":"doc-123"}""");
        });

        var client = Make(handler);
        var id = await client.CreatePurchaseDocumentAsync(new HoldedPurchaseDocumentInput
        {
            ContactId = "contact-1",
            ContactName = "Alice",
            Date = Instant.FromUtc(2026, 5, 10, 0, 0),
            Notes = "Expense report abc-123",
            Lines = [new() { Description = "Train", Amount = 19.52m, AccountId = "acc-629001" }]
        }, Xunit.TestContext.Current.CancellationToken);

        id.Should().Be("doc-123");
        capturedBody.Should().Contain("\"contact_id\":\"contact-1\"");
        capturedBody.Should().Contain("\"date\":\"2026-05-10\"");
        capturedBody.Should().Contain("\"account\":\"acc-629001\"");
        capturedBody.Should().Contain("\"notes\":\"Expense report abc-123\"");
        // Tags are dead on the write side (Peter, 2026-08-10) — the mapped account books the doc
        // to the right category from creation, so no tags are sent at all.
        capturedBody.Should().NotContain("tags");
    }

    [HumansFact]
    public async Task GetPurchaseDocumentAsync_ParsesResponse()
    {
        var json = """
        {
          "id":"doc-123","document_number":"F260009",
          "subtotal":"19.52","tax":"0.00","total":"19.52","currency":"EUR",
          "payments_total":"19.52","payments_pending":"0.00",
          "approved_at":"2026-05-10T09:00:00",
          "tags":["camp-build-camp"],
          "payments_detail":[
            {"id":"6ad492bfadfbf95ccf087fde","amount":"17.00","date":"2026-10-16","bank_id":"69d65af9b42dd1dd620d9330"},
            {"id":"6ad492bfadfbf95ccf087fdf","amount":"2.52","date":"2026-10-17","bank_id":null}
          ]
        }
        """;
        var handler = new StubHandler(req =>
        {
            req.Method.Method.Should().Be("GET");
            req.RequestUri!.PathAndQuery
                .Should().Be("/api/v2/purchases/doc-123");
            return Respond(HttpStatusCode.OK, json);
        });

        var client = Make(handler);
        var doc = await client.GetPurchaseDocumentAsync("doc-123", Xunit.TestContext.Current.CancellationToken);

        doc.Id.Should().Be("doc-123");
        doc.DocNumber.Should().Be("F260009");
        doc.PaymentsPending.Should().Be(0);
        doc.ApprovedAt.Should().NotBeNull();
        doc.Tags.Should().ContainSingle("camp-build-camp");
        // The live payments_detail shape (2026-10-18): a 2-dp amount string, a plain date, bank_id.
        doc.Payments.Should().HaveCount(2);
        doc.Payments[0].Should().Be(new HoldedPaymentDetailDto(
            "6ad492bfadfbf95ccf087fde", 17.00m, new LocalDate(2026, 10, 16), "69d65af9b42dd1dd620d9330"));
        doc.Payments[1].BankId.Should().BeNull();
    }

    [HumansFact]
    public async Task GetPurchaseDocumentAsync_NoPaymentsDetail_ReadsAsNoPayments()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, """{"id":"doc-123","total":"5.00"}"""));

        var doc = await Make(handler).GetPurchaseDocumentAsync("doc-123", Xunit.TestContext.Current.CancellationToken);

        doc.Payments.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetPurchaseDocumentAsync_MalformedPaymentsDetailEntry_IsDropped_NotTheDocument()
    {
        // One GUI-entered payment in an unexpected shape must not fail every reader of the document
        // (the publisher, the paid poll, the before-read of a payment mid-allocation).
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, """
            {"id":"doc-123","total":"5.00","payments_detail":[
              {"id":"pay-bad-date","amount":"5.00","date":"yesterday","bank_id":"b1"},
              {"id":"pay-bad-amount","amount":{"x":1},"date":"2026-10-16","bank_id":"b1"},
              {"id":42,"amount":"5.00","date":"2026-10-16"},
              {"id":"pay-ok","amount":"5.00","date":"2026-10-16","bank_id":"b1"}]}
            """));

        var doc = await Make(handler).GetPurchaseDocumentAsync("doc-123", Xunit.TestContext.Current.CancellationToken);

        doc.Payments.Select(p => p.Id).Should().Equal("pay-ok");
    }

    [HumansFact]
    public async Task GetPurchaseDocumentAsync_surfaces_an_unreadable_body_as_permanent_not_a_raw_parse_throw()
    {
        // Callers handle only IHoldedClient's two typed exceptions; a raw parse throw from a bad
        // stored value would escape the client and leak an Infrastructure detail upward.
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, """{"id":42,"document_number":"F001","currency":"EUR"}"""));
        var client = Make(handler);

        var act = async () => await client.GetPurchaseDocumentAsync("doc-123", Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HoldedPermanentException>();
    }

    [HumansFact]
    public async Task GetPurchaseDocumentAsync_404Throws_HoldedPermanent()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.NotFound, "{}"));
        var client = Make(handler);

        var act = async () => await client.GetPurchaseDocumentAsync("missing", Xunit.TestContext.Current.CancellationToken);

        var ex = await act.Should().ThrowAsync<HoldedPermanentException>();
        ex.Which.StatusCode.Should().Be(404);
    }

    [HumansFact]
    public async Task ApprovePurchaseDocumentAsync_AlreadyApproved_ReturnsNormally()
    {
        // Holded's production reply to re-approving a doc a retried push already approved.
        var handler = new StubHandler(_ => Respond(HttpStatusCode.BadRequest,
            """{"type":"https:\/\/api.holded.com\/problems\/bad-request","title":"Bad request","status":400,"detail":"Document already approved"}"""));
        var client = Make(handler);

        var act = async () => await client.ApprovePurchaseDocumentAsync("doc-123", Xunit.TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    [HumansFact]
    public async Task ApprovePurchaseDocumentAsync_OtherBadRequest_StillThrows()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.BadRequest,
            """{"status":400,"detail":"Document has no lines"}"""));
        var client = Make(handler);

        var act = async () => await client.ApprovePurchaseDocumentAsync("doc-123", Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HoldedPermanentException>();
    }

    [HumansFact]
    public async Task PermanentFailure_MasksAnIbanEchoedBackInTheResponseBody()
    {
        // The message becomes the outbox LastError, the audit description and the finance-admin
        // card. A 4xx on a contact upsert can echo the IBAN we just sent
        // (memory/code/iban-mask-in-logs.md). ResponseBody keeps the untouched body.
        var body = """{"status":0,"info":"invalid iban ES9121000418450200051332"}""";
        var handler = new StubHandler(_ => Respond(HttpStatusCode.BadRequest, body));
        var client = Make(handler);

        var act = async () => await client.GetPurchaseDocumentAsync("doc-1", Xunit.TestContext.Current.CancellationToken);

        var ex = await act.Should().ThrowAsync<HoldedPermanentException>();
        ex.Which.Message.Should().NotContain("ES9121000418450200051332");
        ex.Which.Message.Should().Contain("ES91****332");
        ex.Which.ResponseBody.Should().Be(body);
    }

    [HumansFact]
    public async Task GetPurchaseDocumentAsync_500Throws_HoldedTransient()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.ServiceUnavailable, ""));
        var client = Make(handler);

        var act = async () => await client.GetPurchaseDocumentAsync("doc-123", Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HoldedTransientException>();
    }

    /// <summary>Routes a payment round-trip: the document is read before and after the POST, and the
    /// POST itself answers <c>{"status":"paid"}</c> and nothing else — the live shape (2026-10-18).
    /// <paramref name="afterDetail"/> is what the second read's <c>payments_detail</c> holds.</summary>
    private static StubHandler PaymentHandler(
        string beforeDetail, string afterDetail, Action<HttpRequestMessage>? onPost = null,
        Func<int, HttpResponseMessage?>? overrideGet = null)
    {
        var gets = 0;
        return new StubHandler(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                req.RequestUri!.PathAndQuery.Should().Be("/api/v2/purchases/doc-123/payments");
                onPost?.Invoke(req);
                return Respond(HttpStatusCode.Created, """{"status":"paid"}""");
            }
            req.RequestUri!.PathAndQuery.Should().Be("/api/v2/purchases/doc-123");
            gets++;
            if (overrideGet?.Invoke(gets) is { } forced) return forced;
            var detail = gets == 1 ? beforeDetail : afterDetail;
            return Respond(HttpStatusCode.OK, $$"""{"id":"doc-123","total":"50.00","payments_detail":[{{detail}}]}""");
        });
    }

    private const string OldPayment =
        """{"id":"pay-old","amount":"18.00","date":"2026-08-25","bank_id":"treasury-1"}""";
    private const string NewPayment =
        """{"id":"pay-9","amount":"18.00","date":"2026-08-25","bank_id":"treasury-1"}""";

    [HumansFact]
    public async Task PayPurchaseDocumentAsync_PostsTheAmountAsAString_AndReadsTheIdOffTheDocument()
    {
        // The POST response carries no id — never did. The id is the payments_detail entry that is
        // new after the POST and matches the amount, date and treasury account just sent, even when
        // an identical earlier payment sits on the document.
        string? capturedBody = null;
        var handler = PaymentHandler(
            OldPayment, OldPayment + "," + NewPayment,
            onPost: req => capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());

        var id = await Make(handler).PayPurchaseDocumentAsync(
            "doc-123", 18m, "treasury-1", new LocalDate(2026, 8, 25), "SEPA payout E1",
            Xunit.TestContext.Current.CancellationToken);

        id.Should().Be("pay-9");
        // v2 rejects a JSON number here — the amount has to go over the wire as a decimal string.
        capturedBody.Should().Contain("\"amount\":\"18.00\"");
        capturedBody.Should().Contain("\"treasury_id\":\"treasury-1\"");
        capturedBody.Should().Contain("\"date\":\"2026-08-25\"");
    }

    [HumansFact]
    public async Task PayPurchaseDocumentAsync_NoTreasuryAccount_OmitsTheKeyRatherThanSendingNull()
    {
        // A null treasury_id is not "use the default" to Holded — the key is simply left out, and
        // the after-read then matches on amount and date alone, whatever bank Holded picked.
        string? capturedBody = null;
        var handler = PaymentHandler(
            "", """{"id":"pay-9","amount":"5.00","date":"2026-08-25","bank_id":"whatever"}""",
            onPost: req => capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());

        var id = await Make(handler).PayPurchaseDocumentAsync(
            "doc-123", 5m, "", new LocalDate(2026, 8, 25), null,
            Xunit.TestContext.Current.CancellationToken);

        id.Should().Be("pay-9");
        capturedBody.Should().NotContain("treasury_id");
        capturedBody.Should().NotContain("description");
    }

    [HumansTheory]
    [Xunit.InlineData("")]                                          // nothing new on the document
    [Xunit.InlineData("""{"id":"pay-9","amount":"18.01","date":"2026-08-25","bank_id":"treasury-1"}""")]
    [Xunit.InlineData("""{"id":"pay-9","amount":"18.00","date":"2026-08-24","bank_id":"treasury-1"}""")]
    [Xunit.InlineData("""{"id":"pay-9","amount":"18.00","date":"2026-08-25","bank_id":"treasury-2"}""")]
    [Xunit.InlineData("""{"id":"pay-9","amount":"18.00","date":"2026-08-25","bank_id":"treasury-1"},{"id":"pay-10","amount":"18.00","date":"2026-08-25","bank_id":"treasury-1"}""")]
    public async Task PayPurchaseDocumentAsync_NoSingleNewMatchingPayment_ReturnsAnUnconfirmedRef(string afterDetail)
    {
        // A 2xx means the payment posted. Throwing would lose it from the record and let the caller
        // pay again — the sentinel keeps it, naming the document a human has to check in Holded.
        // Zero new matching entries and two of them are both "cannot tell which": the sentinel.
        var handler = PaymentHandler(OldPayment, OldPayment + (afterDetail.Length == 0 ? "" : "," + afterDetail));

        var id = await Make(handler).PayPurchaseDocumentAsync(
            "doc-123", 18m, "treasury-1", new LocalDate(2026, 8, 25), null,
            Xunit.TestContext.Current.CancellationToken);

        id.Should().Be("unconfirmed:doc-123");
    }

    [HumansFact]
    public async Task PayPurchaseDocumentAsync_DocumentReadFailsBeforeThePost_ThrowsAndPostsNothing()
    {
        // Without the before-list the new payment could not be told apart afterwards, so the read
        // failing is a refusal: nothing is posted, and the caller handles it like any refused call.
        var posts = 0;
        var handler = PaymentHandler(
            "", "", onPost: _ => posts++,
            overrideGet: n => n == 1 ? Respond(HttpStatusCode.ServiceUnavailable, "") : null);

        var act = async () => await Make(handler).PayPurchaseDocumentAsync(
            "doc-123", 5m, "treasury-1", new LocalDate(2026, 8, 25), null,
            Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HoldedTransientException>();
        posts.Should().Be(0);
    }

    [HumansTheory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    public async Task AcceptedFinancialPosting_UnreadableResponse_ReturnsAnUnconfirmedRef(bool ledgerEntry, bool invalidCharset)
    {
        // For a ledger entry the unreadable body is the POST's own; for a payment it is the
        // document re-read after the POST. Either way the money is posted and the ref is a sentinel.
        var posts = 0;
        HttpResponseMessage Unreadable()
        {
            var response = Respond(HttpStatusCode.OK, invalidCharset ? "{\"id\":\"posted-1\"}" : "not json");
            if (invalidCharset) response.Content.Headers.ContentType!.CharSet = "unsupported-charset";
            return response;
        }
        var handler = ledgerEntry
            ? new StubHandler(_ => { posts++; return Unreadable(); })
            : PaymentHandler("", "", onPost: _ => posts++, overrideGet: n => n == 2 ? Unreadable() : null);
        var client = Make(handler);
        var date = new LocalDate(2026, 8, 25);
        var ct = Xunit.TestContext.Current.CancellationToken;

        var id = ledgerEntry
            ? await client.PostLedgerEntryAsync(date, 40000004, 57200001, 5m, "SEPA payout", ct)
            : await client.PayPurchaseDocumentAsync("doc-123", 5m, "treasury-1", date, null, ct);

        id.Should().Be(ledgerEntry ? "unconfirmed:entry" : "unconfirmed:doc-123");
        posts.Should().Be(1);
    }

    [HumansFact]
    public async Task PostLedgerEntryAsync_PostsTwoBalancedLinesAsStringsAndReturnsTheEntryId()
    {
        string? capturedBody = null;
        var handler = new StubHandler(req =>
        {
            req.Method.Method.Should().Be("POST");
            req.RequestUri!.PathAndQuery.Should().Be("/api/v2/ledger-entries");
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Respond(HttpStatusCode.Created, """{"id":"entry-7"}""");
        });

        var id = await Make(handler).PostLedgerEntryAsync(
            new LocalDate(2026, 9, 18), 40000004, 57200001, 20m, "SEPA payout E1",
            Xunit.TestContext.Current.CancellationToken);

        id.Should().Be("entry-7");
        capturedBody.Should().Contain("\"date\":\"2026-09-18\"");
        capturedBody.Should().Contain("\"notes\":\"SEPA payout E1\"");
        // Accounts go as ledger numbers, amounts as decimal strings — the shape v2 accepts.
        capturedBody.Should().Contain("\"account\":40000004").And.Contain("\"debit\":\"20.00\",\"credit\":\"0.00\"");
        capturedBody.Should().Contain("\"account\":57200001").And.Contain("\"debit\":\"0.00\",\"credit\":\"20.00\"");
    }

    [HumansFact]
    public async Task PostLedgerEntryAsync_ResponseWithoutAnId_ReturnsAnUnconfirmedRef()
    {
        // A 2xx means the entry posted; throwing would let the caller post it twice.
        var handler = new StubHandler(_ => Respond(HttpStatusCode.Created, "{}"));

        var id = await Make(handler).PostLedgerEntryAsync(
            new LocalDate(2026, 9, 18), 40000004, 57200001, 20m, "SEPA payout E1",
            Xunit.TestContext.Current.CancellationToken);

        id.Should().Be("unconfirmed:entry");
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
