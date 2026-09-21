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

public class HoldedClientTreasuryTests
{
    private static HoldedClient Make(StubHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.holded.com") },
            Options.Create(new HoldedClientOptions { ApiKey = "test-key" }),
            NullLogger<HoldedClient>.Instance,
            new HoldedCallLog(),
            new FakeClock(Instant.FromUtc(2026, 8, 10, 12, 0)));

    [HumansFact]
    public async Task ListBankMovementsAsync_RequestsTheAccountsFeed_WithTheWindow()
    {
        string? capturedPath = null;
        string? capturedQuery = null;
        var handler = new StubHandler(req =>
        {
            capturedPath = req.RequestUri!.AbsolutePath;
            capturedQuery = req.RequestUri!.Query;
            return Respond(HttpStatusCode.OK, """{"items":[],"cursor":null,"has_more":false}""");
        });

        var client = Make(handler);
        await client.ListBankMovementsAsync(
            "tr-1", new LocalDate(2026, 8, 1), new LocalDate(2026, 8, 31),
            Xunit.TestContext.Current.CancellationToken);

        capturedPath.Should().Be("/api/v2/treasury/accounts/tr-1/bank-movements");
        capturedQuery.Should().Contain("start_date=2026-08-01");
        // end_date is exclusive on the live API, same rule as ListLedgerEntriesAsync.
        capturedQuery.Should().Contain("end_date=2026-09-01");
        capturedQuery.Should().Contain("limit=200");
    }

    [HumansFact]
    public async Task ListBankMovementsAsync_ParsesFields_AndLowercasesStatus()
    {
        var json = """
        {"items":[{"id":"m1","account":"tr-1","date":"2026-08-18","amount":"-120.00",
          "description":"12345678 - NCA - Alice","status":"PENDING","origin":"bank"}],
         "cursor":null,"has_more":false}
        """;
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var movements = await client.ListBankMovementsAsync(
            "tr-1", new LocalDate(2026, 8, 1), new LocalDate(2026, 8, 31),
            Xunit.TestContext.Current.CancellationToken);

        movements.Should().HaveCount(1);
        var m = movements[0];
        m.Id.Should().Be("m1");
        m.AccountId.Should().Be("tr-1");
        m.Date.Should().Be(new LocalDate(2026, 8, 18));
        m.Amount.Should().Be(-120.00m);
        m.Description.Should().Be("12345678 - NCA - Alice");
        m.Status.Should().Be("pending");
        m.Origin.Should().Be("bank");
    }

    [HumansFact]
    public async Task ListBankMovementsAsync_ParsesBothDateShapes()
    {
        var json = """
        {"items":[
          {"id":"m1","account":"tr-1","date":"18/09/2026","amount":"-10.00","status":"pending"},
          {"id":"m2","account":"tr-1","date":"2026-09-18","amount":"-10.00","status":"pending"}
        ],"cursor":null,"has_more":false}
        """;
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var movements = await client.ListBankMovementsAsync(
            "tr-1", new LocalDate(2026, 9, 1), new LocalDate(2026, 9, 30),
            Xunit.TestContext.Current.CancellationToken);

        movements.Should().HaveCount(2);
        movements.Should().OnlyContain(m => m.Date == new LocalDate(2026, 9, 18));
    }

    [HumansFact]
    public async Task ListBankMovementsAsync_FiltersOutsideTheWindow_ClientSide()
    {
        var json = """
        {"items":[
          {"id":"m1","account":"tr-1","date":"2026-07-31","amount":"-10.00","status":"pending"},
          {"id":"m2","account":"tr-1","date":"2026-08-15","amount":"-10.00","status":"pending"}
        ],"cursor":null,"has_more":false}
        """;
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var movements = await client.ListBankMovementsAsync(
            "tr-1", new LocalDate(2026, 8, 1), new LocalDate(2026, 8, 31),
            Xunit.TestContext.Current.CancellationToken);

        movements.Should().ContainSingle();
        movements[0].Id.Should().Be("m2");
    }

    [HumansFact]
    public async Task ListBankMovementsAsync_UnparsableLine_IsPermanent()
    {
        var json = """
        {"items":[{"id":"m1","account":"tr-1","date":"not-a-date","amount":"-10.00","status":"pending"}],
         "cursor":null,"has_more":false}
        """;
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var act = async () => await client.ListBankMovementsAsync(
            "tr-1", new LocalDate(2026, 8, 1), new LocalDate(2026, 8, 31),
            Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HoldedPermanentException>();
    }

    [HumansFact]
    public async Task ListBankMovementsAsync_LineWithNoStatus_IsPermanent_NotAssumedPending()
    {
        // "pending" is the one status the SEPA sweep reads as bookable, so a manufactured one could
        // book a transfer against a line Holded has already settled. Absent means unreadable.
        var json = """
        {"items":[{"id":"m1","account":"tr-1","date":"2026-08-18","amount":"-10.00"}],
         "cursor":null,"has_more":false}
        """;
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var act = async () => await client.ListBankMovementsAsync(
            "tr-1", new LocalDate(2026, 8, 1), new LocalDate(2026, 8, 31),
            Xunit.TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<HoldedPermanentException>())
            .WithMessage("*status*");
    }

    [HumansFact]
    public async Task ListBankMovementsAsync_LineWithBlankId_IsPermanent_NotBookedAgainst()
    {
        // A blank id would still pass a null check, persist as HoldedBankMovementId, and send the
        // sweep's reconcile call to an invalid URL — same bug class as an absent status.
        var json = """
        {"items":[{"id":"","account":"tr-1","date":"2026-08-18","amount":"-10.00","status":"pending"}],
         "cursor":null,"has_more":false}
        """;
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var act = async () => await client.ListBankMovementsAsync(
            "tr-1", new LocalDate(2026, 8, 1), new LocalDate(2026, 8, 31),
            Xunit.TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<HoldedPermanentException>())
            .WithMessage("*id*");
    }

    [HumansFact]
    public async Task ListBankMovementsAsync_MalformedSuccessBody_IsAHoldedException_NotRawJson()
    {
        // The paged walk parses the envelope before any caller's try block. Unnormalized, the
        // JsonException escapes the client and /Finance/Sepa fails the request instead of showing
        // its bank-feed warning.
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, """{"items":[{"id":""")));

        var act = async () => await client.ListBankMovementsAsync(
            "tr-1", new LocalDate(2026, 8, 1), new LocalDate(2026, 8, 31),
            Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HoldedApiException>();
    }

    [HumansFact]
    public async Task ListBankMovementsAsync_EnvelopeFieldOfTheWrongType_IsAHoldedException()
    {
        // Same gap, reached through has_more rather than the parser itself.
        var json = """{"items":[],"cursor":null,"has_more":"yes"}""";
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var act = async () => await client.ListBankMovementsAsync(
            "tr-1", new LocalDate(2026, 8, 1), new LocalDate(2026, 8, 31),
            Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HoldedApiException>();
    }

    [HumansFact]
    public async Task ListBankMovementsAsync_FollowsTheCursor()
    {
        var callCount = 0;
        string? secondQuery = null;
        var handler = new StubHandler(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                return Respond(HttpStatusCode.OK, """
                {"items":[{"id":"m1","account":"tr-1","date":"2026-08-01","amount":"-10.00","status":"pending"}],
                 "cursor":"c1","has_more":true}
                """);
            }
            secondQuery = req.RequestUri!.Query;
            return Respond(HttpStatusCode.OK, """
            {"items":[{"id":"m2","account":"tr-1","date":"2026-08-02","amount":"-20.00","status":"pending"}],
             "cursor":null,"has_more":false}
            """);
        });

        var client = Make(handler);
        var movements = await client.ListBankMovementsAsync(
            "tr-1", new LocalDate(2026, 8, 1), new LocalDate(2026, 8, 31),
            Xunit.TestContext.Current.CancellationToken);

        movements.Should().HaveCount(2);
        callCount.Should().Be(2);
        secondQuery.Should().Contain("cursor=c1");
    }

    [HumansFact]
    public async Task ReconcileBankMovementAsync_PostsDocumentsArray()
    {
        string? capturedPath = null;
        string? capturedMethod = null;
        string? capturedBody = null;
        var handler = new StubHandler(req =>
        {
            capturedPath = req.RequestUri!.AbsolutePath;
            capturedMethod = req.Method.Method;
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Respond(HttpStatusCode.OK, "{}");
        });

        var client = Make(handler);
        await client.ReconcileBankMovementAsync(
            "tr-1", "m1",
            [
                new HoldedReconcileDocumentRef("d1", HoldedReconcileDocumentType.Purchase),
                new HoldedReconcileDocumentRef("d2", HoldedReconcileDocumentType.LedgerEntry),
            ],
            Xunit.TestContext.Current.CancellationToken);

        capturedPath.Should().Be("/api/v2/treasury/accounts/tr-1/bank-movements/m1/reconcile");
        capturedMethod.Should().Be("POST");
        capturedBody.Should().Be(
            """{"documents":[{"document_id":"d1","document_type":"purchase"},{"document_id":"d2","document_type":"dailyledger"}]}""");
    }

    [HumansFact]
    public async Task ReconcileBankMovementAsync_HoldedRefusal_IsPermanent()
    {
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.BadRequest, "{}")));

        var act = async () => await client.ReconcileBankMovementAsync(
            "tr-1", "m1", [new HoldedReconcileDocumentRef("d1", HoldedReconcileDocumentType.Purchase)],
            Xunit.TestContext.Current.CancellationToken);

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
