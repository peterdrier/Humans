using System.Net.Http;
using AwesomeAssertions;
using Humans.Infrastructure.Services.Mailer;

namespace Humans.Application.Tests.Services.Mailer;

public class MailerLiteClientWriteGuardTests
{
    [HumansFact]
    public async Task SendAsync_ThrowsOnNonGetRequest()
    {
        var factory = new StubHttpClientFactory(new RecordingHandler());
        var client = new MailerLiteClient(factory, NodaTime.SystemClock.Instance,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MailerLiteClient>.Instance);

        var act = async () => await client.SendForTestsAsync(HttpMethod.Post, "/api/subscribers", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*read-only*");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://example.test/") };
    }
}
