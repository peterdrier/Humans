using System.Net;
using System.Text;
using AwesomeAssertions;
using Humans.Application.Configuration;
using Humans.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Humans.Application.Tests.Services.Finance;

public class HoldedClientTests
{
    private static HoldedClient MakeClient(HttpMessageHandler handler, string apiKey = "test-key")
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.holded.com/") };
        var settings = Options.Create(new HoldedSettings { ApiKey = apiKey, Enabled = true });
        return new HoldedClient(http, settings, NullLogger<HoldedClient>.Instance);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [HumansFact]
    public async Task GetAllPurchaseDocs_PaginatesUntilEmpty()
    {
        var calls = 0;
        var handler = new InlineHandler((req, _) =>
        {
            calls++;
            return calls switch
            {
                1 => Json("""[{"id":"a","docNumber":"F1","date":1700000000,"currency":"eur","subtotal":100,"tax":0,"total":100,"paymentsTotal":0,"paymentsPending":100,"paymentsRefunds":0,"tags":[]}]"""),
                2 => Json("""[{"id":"b","docNumber":"F2","date":1700000001,"currency":"eur","subtotal":50,"tax":0,"total":50,"paymentsTotal":0,"paymentsPending":50,"paymentsRefunds":0,"tags":[]}]"""),
                _ => Json("[]"),
            };
        });

        var client = MakeClient(handler);
        var docs = await client.GetAllPurchaseDocsAsync();

        docs.Should().HaveCount(2);
        docs[0].Dto.Id.Should().Be("a");
        docs[1].Dto.Id.Should().Be("b");
        calls.Should().Be(3); // 2 with data + 1 empty
    }

    [HumansFact]
    public async Task GetAllPurchaseDocs_SendsKeyHeader()
    {
        HttpRequestMessage? captured = null;
        var handler = new InlineHandler((req, _) =>
        {
            captured = req;
            return Json("[]");
        });

        var client = MakeClient(handler, apiKey: "secret-token");
        await client.GetAllPurchaseDocsAsync();

        captured.Should().NotBeNull();
        captured!.Headers.GetValues("key").Should().ContainSingle().Which.Should().Be("secret-token");
    }

    [HumansFact]
    public async Task TryAddTag_OnHttpError_ReturnsFalseAndDoesNotThrow()
    {
        var step = 0;
        var handler = new InlineHandler((req, _) =>
        {
            step++;
            return step switch
            {
                // GET to read existing tags
                1 => Json("""{"id":"a","tags":["existing"],"docNumber":"F1","date":1,"currency":"eur","subtotal":0,"tax":0,"total":0,"paymentsTotal":0,"paymentsPending":0,"paymentsRefunds":0}"""),
                // PUT fails
                _ => Json("""{"error":"not allowed"}""", HttpStatusCode.BadRequest),
            };
        });

        var client = MakeClient(handler);
        var result = await client.TryAddTagAsync("a", "departments-sound");

        result.Should().BeFalse();
    }

    private sealed class InlineHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _impl;
        public InlineHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> impl) => _impl = impl;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_impl(request, ct));
    }
}
