using System.Net;
using System.Text;
using AwesomeAssertions;
using Humans.Application.Interfaces.Holded;
using Humans.Infrastructure.Services.Holded;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Application.Tests.Services.Holded;

public class HoldedClientTests
{
    private static HoldedClient Make(StubHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.holded.com") },
            Options.Create(new HoldedClientOptions { ApiKey = "test-key" }),
            NullLogger<HoldedClient>.Instance);

    [HumansFact]
    public async Task CreatePurchaseDocumentAsync_PostsExpectedJson_AndReturnsId()
    {
        var handler = new StubHandler(req =>
        {
            req.Method.Method.Should().Be("POST");
            req.RequestUri!.PathAndQuery.Should().Be("/api/invoicing/v1/documents/purchase");
            req.Headers.GetValues("key").Single().Should().Be("test-key");
            return Respond(HttpStatusCode.OK, """{"status":1,"id":"doc-123"}""");
        });

        var client = Make(handler);
        var id = await client.CreatePurchaseDocumentAsync(new HoldedPurchaseDocumentInput
        {
            ContactName = "Alice",
            Date = Instant.FromUtc(2026, 5, 10, 0, 0),
            Lines = [new() { Description = "Train", Amount = 19.52m }]
        });

        id.Should().Be("doc-123");
    }

    [HumansFact]
    public async Task GetPurchaseDocumentAsync_ParsesResponse()
    {
        var json = """
        {
          "id":"doc-123","docNumber":"F260009",
          "subtotal":19.52,"tax":0,"total":19.52,
          "paymentsTotal":19.52,"paymentsPending":0,
          "approvedAt":1746835200,
          "tags":["camp-build-camp"]
        }
        """;
        var handler = new StubHandler(req =>
        {
            req.Method.Method.Should().Be("GET");
            req.RequestUri!.PathAndQuery
                .Should().Be("/api/invoicing/v1/documents/purchase/doc-123");
            return Respond(HttpStatusCode.OK, json);
        });

        var client = Make(handler);
        var doc = await client.GetPurchaseDocumentAsync("doc-123");

        doc.Id.Should().Be("doc-123");
        doc.PaymentsPending.Should().Be(0);
        doc.ApprovedAt.Should().NotBeNull();
        doc.Tags.Should().ContainSingle("camp-build-camp");
    }

    [HumansFact]
    public async Task GetPurchaseDocumentAsync_404Throws_HoldedPermanent()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.NotFound, "{}"));
        var client = Make(handler);

        var act = async () => await client.GetPurchaseDocumentAsync("missing");

        var ex = await act.Should().ThrowAsync<HoldedPermanentException>();
        ex.Which.StatusCode.Should().Be(404);
    }

    [HumansFact]
    public async Task GetPurchaseDocumentAsync_500Throws_HoldedTransient()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.ServiceUnavailable, ""));
        var client = Make(handler);

        var act = async () => await client.GetPurchaseDocumentAsync("doc-123");

        await act.Should().ThrowAsync<HoldedTransientException>();
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
