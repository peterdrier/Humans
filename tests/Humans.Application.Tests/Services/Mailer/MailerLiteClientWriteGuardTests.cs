using System.Net.Http;
using AwesomeAssertions;
using Humans.Application.Interfaces.Mailer;
using Humans.Infrastructure.Services.Mailer;
using Microsoft.Extensions.Options;

namespace Humans.Application.Tests.Services.Mailer;

public class MailerLiteClientWriteGuardTests
{
    [HumansFact]
    public async Task SendAsync_ThrowsOnNonGetRequest()
    {
        var opts = Options.Create(new MailerLiteOptions { ApiKey = "x" });
        var http = new HttpClient(new RecordingHandler());
        var client = new MailerLiteClient(http, opts, NodaTime.SystemClock.Instance,
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
}
