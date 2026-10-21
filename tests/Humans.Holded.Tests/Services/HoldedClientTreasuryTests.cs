using System.Net;
using System.Text;
using AwesomeAssertions;
using Humans.Holded.Contracts;
using Humans.Holded.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;

namespace Humans.Holded.Tests.Services;

public class HoldedClientTreasuryTests
{
    private static HoldedClient Make(StubHandler handler, ILogger<HoldedClient>? logger = null) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.holded.com") },
            Options.Create(new HoldedClientOptions { ApiKey = "test-key" }),
            logger ?? NullLogger<HoldedClient>.Instance,
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
        var json = $$"""
        {"items":[{{Line("m1", "-120.00", "PENDING", booking: "2026-08-18T00:00:00+00:00", description: "12345678 - NCA - Alice", origin: "bank")}}],
         "cursor":null,"has_more":false}
        """;
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var movements = await client.ListBankMovementsAsync(
            "tr-1", new LocalDate(2026, 8, 1), new LocalDate(2026, 8, 31),
            Xunit.TestContext.Current.CancellationToken);

        movements.Should().HaveCount(1);
        var m = movements[0];
        m.Id.Should().Be("m1");
        m.Date.Should().Be(new LocalDate(2026, 8, 18));
        m.Amount.Should().Be(-120.00m);
        m.Description.Should().Be("12345678 - NCA - Alice");
        m.Status.Should().Be("pending");
        m.Origin.Should().Be("bank");
    }

    [HumansFact]
    public async Task ListBankMovementsAsync_ReadsBookingDate_TheFeedHasNoDateKey()
    {
        // The real record behind every "undated" Sabadell line (peterdrier/Humans#1861, #1946): the
        // v2 feed has no `date` key at all, only booking_date and value_date, ISO 8601 with an offset.
        var json = $$"""
        {"items":[
          {{Line("6ad250e8219fce7fbb0c18d3", "-17.00", "pending",
              booking: "2026-10-16T00:00:00+00:00", value: "2026-10-16T00:00:00+00:00")}},
          {{Line("stripe-1", "40.00", "reconciled",
              booking: "2026-10-20T16:13:31+00:00", value: "2026-10-20T16:13:31+00:00", origin: "gateway")}}
        ],"cursor":null,"has_more":false}
        """;
        var logger = new CapturingLogger<HoldedClient>();
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)), logger);

        var movements = await client.ListBankMovementsAsync(
            "tr-1", new LocalDate(2026, 10, 1), new LocalDate(2026, 10, 31),
            Xunit.TestContext.Current.CancellationToken);

        movements.Select(m => m.Date).Should().Equal(new LocalDate(2026, 10, 16), new LocalDate(2026, 10, 20));
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
    }

    [HumansFact]
    public async Task ListBankMovementsAsync_FallsBackToValueDate_WhenBookingDateIsAbsentOrBlank()
    {
        var json = $$"""
        {"items":[
          {{Line("m1", "-10.00", "pending", booking: null, value: "2026-10-13T00:00:00+00:00")}},
          {{Line("m2", "-10.00", "pending", booking: "", value: "2026-10-14T00:00:00+00:00")}}
        ],"cursor":null,"has_more":false}
        """;
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var movements = await client.ListBankMovementsAsync(
            "tr-1", new LocalDate(2026, 10, 1), new LocalDate(2026, 10, 31),
            Xunit.TestContext.Current.CancellationToken);

        movements.Select(m => m.Date).Should().Equal(new LocalDate(2026, 10, 13), new LocalDate(2026, 10, 14));
    }

    [HumansFact]
    public async Task ListBankMovementsAsync_TakesTheDatePartAsGiven_NotShiftedToMadrid()
    {
        // A Sabadell line is midnight UTC; shifting it to Europe/Madrid would not move the day, but a
        // late-evening gateway line would land on the next day, and Holded books it on the UTC date.
        var json = $$"""
        {"items":[{{Line("m1", "40.00", "pending", booking: "2026-07-20T23:30:00+00:00", value: null)}}],
         "cursor":null,"has_more":false}
        """;
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var movements = await client.ListBankMovementsAsync(
            "tr-1", new LocalDate(2026, 7, 1), new LocalDate(2026, 7, 31),
            Xunit.TestContext.Current.CancellationToken);

        movements.Single().Date.Should().Be(new LocalDate(2026, 7, 20));
    }

    [HumansFact]
    public async Task ListBankMovementsAsync_FiltersOutsideTheWindow_ClientSide()
    {
        var json = $$"""
        {"items":[
          {{Line("m1", "-10.00", "pending", booking: "2026-07-31T00:00:00+00:00")}},
          {{Line("m2", "-10.00", "pending", booking: "2026-08-15T00:00:00+00:00")}}
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
    public async Task ListBankMovementsAsync_LineWithNoAccount_StillReads()
    {
        // The feed is already scoped to one account by its URL, and nothing reads a line's
        // `account`. One Sabadell line without it failed every SEPA sweep (peterdrier/Humans#1853).
        var json = $$"""
        {"items":[
          {{Line("m1", "-10.00", "pending", booking: "2026-08-18T00:00:00+00:00")}},
          {{Line("m2", "-20.00", "pending", booking: "2026-08-18T00:00:00+00:00")}}
        ],"cursor":null,"has_more":false}
        """;
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var movements = await client.ListBankMovementsAsync(
            "tr-1", new LocalDate(2026, 8, 1), new LocalDate(2026, 8, 31),
            Xunit.TestContext.Current.CancellationToken);

        movements.Select(m => m.Id).Should().Equal("m1", "m2");
    }

    [HumansFact]
    public async Task ListBankMovementsAsync_NonCandidateLineWithUnreadableDate_IsSkipped_AndLogged()
    {
        // A line with neither booking_date nor value_date, or an unparsable one, cannot be windowed.
        // Incoming or already-touched lines can never be a SEPA candidate, so they are skipped.
        var json = $$"""
        {"items":[
          {{Line("m1", "10.00", "pending", booking: null, value: null)}},
          {{Line("m2", "-10.00", "reconciled", booking: "not-a-date", value: "not-a-date")}},
          {{Line("m3", "-20.00", "pending", booking: "2026-08-18T00:00:00+00:00")}}
        ],"cursor":null,"has_more":false}
        """;
        var logger = new CapturingLogger<HoldedClient>();
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)), logger);

        var movements = await client.ListBankMovementsAsync(
            "tr-1", new LocalDate(2026, 8, 1), new LocalDate(2026, 8, 31),
            Xunit.TestContext.Current.CancellationToken);

        movements.Select(m => m.Id).Should().Equal("m3");
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("Skipped 2 Holded bank movement(s)"));
    }

    [HumansFact]
    public async Task ListBankMovementsAsync_PendingOutgoingLineWithUnreadableDate_IsPermanent()
    {
        // Dropping the undated line would leave m2 with no visible rival, so the sweep could book the
        // transfer against the wrong one of two same-amount lines. The page fails instead.
        var json = $$"""
        {"items":[
          {{Line("m1", "-20.00", "pending", booking: null, value: null)}},
          {{Line("m2", "-20.00", "pending", booking: "2026-08-18T00:00:00+00:00")}}
        ],"cursor":null,"has_more":false}
        """;
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var act = async () => await client.ListBankMovementsAsync(
            "tr-1", new LocalDate(2026, 8, 1), new LocalDate(2026, 8, 31),
            Xunit.TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<HoldedPermanentException>())
            .Which.BankMovementId.Should().Be("m1");
    }

    [HumansFact]
    public async Task ListBankMovementsAsync_PendingOutgoingLineWithUnreadableDate_IsSkipped_WhenTheCallerNamesIt()
    {
        // Once Finance has given up on a genuinely undated line (peterdrier/Humans#1946), it asks for
        // that one id to be skipped. Another undated pending line still fails.
        var json = $$"""
        {"items":[
          {{Line("m1", "-20.00", "pending", booking: null, value: null)}},
          {{Line("m2", "-20.00", "pending", booking: "2026-08-18T00:00:00+00:00")}},
          {{Line("m3", "-5.00", "pending", booking: null, value: null)}}
        ],"cursor":null,"has_more":false}
        """;
        var logger = new CapturingLogger<HoldedClient>();
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)), logger);
        var from = new LocalDate(2026, 8, 1);
        var to = new LocalDate(2026, 8, 31);

        var blocked = async () => await client.ListBankMovementsAsync(
            "tr-1", from, to, Xunit.TestContext.Current.CancellationToken,
            new HashSet<string>(StringComparer.Ordinal) { "m1" });
        (await blocked.Should().ThrowAsync<HoldedPermanentException>())
            .Which.BankMovementId.Should().Be("m3");

        var movements = await client.ListBankMovementsAsync(
            "tr-1", from, to, Xunit.TestContext.Current.CancellationToken,
            new HashSet<string>(StringComparer.Ordinal) { "m1", "m3" });

        movements.Select(m => m.Id).Should().Equal("m2");
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("Skipping Holded bank movement m1"));
    }

    [HumansFact]
    public async Task ListBankMovementsAsync_LineWithNoStatus_IsPermanent_NotAssumedPending()
    {
        // "pending" is the one status the SEPA sweep reads as bookable, so a manufactured one could
        // book a transfer against a line Holded has already settled. Absent means unreadable.
        var json = $$"""
        {"items":[{{Line("m1", "-10.00", null, booking: "2026-08-18T00:00:00+00:00")}}],
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
        var json = $$"""
        {"items":[{{Line("", "-10.00", "pending", booking: "2026-08-18T00:00:00+00:00")}}],
         "cursor":null,"has_more":false}
        """;
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var act = async () => await client.ListBankMovementsAsync(
            "tr-1", new LocalDate(2026, 8, 1), new LocalDate(2026, 8, 31),
            Xunit.TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<HoldedPermanentException>())
            .WithMessage("*id*");
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task ListBankMovementsAsync_RejectsNullRowsRatherThanReturningAnIncompleteFeed(bool mixed)
    {
        var rows = mixed
            ? $$"""{{Line("m1", "-10.00", "pending", booking: "2026-08-18T00:00:00+00:00")}},null"""
            : "null";
        var json = "{\"items\":[" + rows + "],\"has_more\":false}";
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var read = async () => await client.ListBankMovementsAsync(
            "tr-1", new LocalDate(2026, 8, 1), new LocalDate(2026, 8, 31),
            Xunit.TestContext.Current.CancellationToken);

        await read.Should().ThrowAsync<HoldedApiException>();
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
                return Respond(HttpStatusCode.OK, $$"""
                {"items":[{{Line("m1", "-10.00", "pending", booking: "2026-08-01T00:00:00+00:00")}}],
                 "cursor":"c1","has_more":true}
                """);
            }
            secondQuery = req.RequestUri!.Query;
            return Respond(HttpStatusCode.OK, $$"""
            {"items":[{{Line("m2", "-20.00", "pending", booking: "2026-08-02T00:00:00+00:00")}}],
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
            capturedBody = new StreamReader(req.Content!.ReadAsStream()).ReadToEnd();
            return Respond(HttpStatusCode.OK, "{}");
        });

        var client = Make(handler);
        await client.ReconcileBankMovementAsync(
            "tr-1", "m1",
            [
                new HoldedReconcileDocumentRef("pay-1", HoldedReconcileDocumentType.Payment),
                new HoldedReconcileDocumentRef("pay-2", HoldedReconcileDocumentType.Payment),
            ],
            Xunit.TestContext.Current.CancellationToken);

        capturedPath.Should().Be("/api/v2/treasury/accounts/tr-1/bank-movements/m1/reconcile");
        capturedMethod.Should().Be("POST");
        capturedBody.Should().Be(
            """{"documents":[{"document_id":"pay-1","document_type":"payment"},{"document_id":"pay-2","document_type":"payment"}]}""");
    }

    [HumansFact]
    public async Task ReconcileBankMovementAsync_HoldedRefusal_IsPermanent()
    {
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.BadRequest, "{}")));

        var act = async () => await client.ReconcileBankMovementAsync(
            "tr-1", "m1", [new HoldedReconcileDocumentRef("pay-1", HoldedReconcileDocumentType.Payment)],
            Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HoldedPermanentException>();
    }

    /// <summary>One feed line in the exact 15-key shape the live v2 API returns (checked 2026-10-17;
    /// there is no <c>date</c> key). A null <paramref name="status"/> omits the key; a null date is a
    /// JSON null, the shape Holded uses for an absent value.</summary>
    private static string Line(
        string id, string amount, string? status, string? booking = "2026-08-18T00:00:00+00:00",
        string? value = "2026-08-18T00:00:00+00:00",
        string description = "Transferencia nobodies collective asociacion", string origin = "afterbanks")
    {
        static string J(string? v) => v is null ? "null" : $"\"{v}\"";
        var statusKey = status is null ? "" : $"\"status\":{J(status)},";
        return $"{{\"id\":{J(id)},\"banking_account_id\":\"tr-1\",\"description\":{J(description)},"
            + $"\"note\":\"\",\"amount\":{J(amount)},\"currency\":\"EUR\",\"balance\":\"251861.05\","
            + "\"accounting_amount\":null,\"accounting_currency\":null,"
            + $"\"booking_date\":{J(booking)},\"value_date\":{J(value)},{statusKey}"
            + $"\"reconciled_amount\":\"0.00\",\"origin\":{J(origin)},\"flagged_at\":null}}";
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
