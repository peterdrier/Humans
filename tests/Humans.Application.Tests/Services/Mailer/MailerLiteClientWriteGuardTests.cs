using System.Net;
using System.Net.Http;
using AwesomeAssertions;
using Humans.Application.Interfaces.Mailer;
using Humans.Infrastructure.Services.Mailer;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Humans.Application.Tests.Services.Mailer;

public class MailerLiteClientWriteGuardTests
{
    [HumansFact]
    public async Task CreateGroupAsync_RejectsNonHumansName()
    {
        var client = NewClient(new ScriptedHandler());

        var act = async () => await client.CreateGroupAsync("Newsletter", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Humans - *");
    }

    [HumansFact]
    public async Task AssignSubscriberToGroupAsync_RejectsWritesToNonHumansGroups()
    {
        var handler = new ScriptedHandler();
        // Cache pre-populate: empty subscriber page, then groups page with one non-Humans group.
        handler.EnqueueJson(HttpStatusCode.OK, """{"data":[],"meta":{"next_cursor":null}}""");
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"data":[{"id":"99","name":"Newsletter","created_at":"2026-01-01 00:00:00","active_count":0,"unsubscribed_count":0,"unconfirmed_count":0,"bounced_count":0,"junk_count":0}],"meta":{"current_page":1,"last_page":1}}""");
        var client = NewClient(handler);

        var act = async () => await client.AssignSubscriberToGroupAsync("sub-1", "99", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Humans - *");
    }

    [HumansFact]
    public async Task UnassignSubscriberFromGroupAsync_RejectsWritesToNonHumansGroups()
    {
        var handler = new ScriptedHandler();
        handler.EnqueueJson(HttpStatusCode.OK, """{"data":[],"meta":{"next_cursor":null}}""");
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"data":[{"id":"99","name":"Newsletter","created_at":"2026-01-01 00:00:00","active_count":0,"unsubscribed_count":0,"unconfirmed_count":0,"bounced_count":0,"junk_count":0}],"meta":{"current_page":1,"last_page":1}}""");
        var client = NewClient(handler);

        var act = async () => await client.UnassignSubscriberFromGroupAsync("sub-1", "99", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [HumansFact]
    public async Task BulkImportSubscribersToGroupAsync_RejectsWritesToNonHumansGroups()
    {
        var handler = new ScriptedHandler();
        handler.EnqueueJson(HttpStatusCode.OK, """{"data":[],"meta":{"next_cursor":null}}""");
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"data":[{"id":"99","name":"Newsletter","created_at":"2026-01-01 00:00:00","active_count":0,"unsubscribed_count":0,"unconfirmed_count":0,"bounced_count":0,"junk_count":0}],"meta":{"current_page":1,"last_page":1}}""");
        var client = NewClient(handler);

        var act = async () => await client.BulkImportSubscribersToGroupAsync(
            "99", new[] { "a@example.com" }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static MailerLiteClient NewClient(HttpMessageHandler handler) =>
        new(new StubHttpClientFactory(handler),
            NodaTime.SystemClock.Instance,
            Options.Create(new MailerLiteOptions()),
            NullLogger<MailerLiteClient>.Instance);

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        public void EnqueueJson(HttpStatusCode status, string body)
            => _responses.Enqueue(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            => Task.FromResult(_responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
                });
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://example.test/") };
    }
}
