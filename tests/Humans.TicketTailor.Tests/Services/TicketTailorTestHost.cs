using System.Net;
using System.Text;
using System.Text.Json;
using Humans.TicketTailor.Services;
using Humans.Tickets.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Humans.TicketTailor.Tests.Services;

internal static class TicketTailorTestHost
{
    public static TicketTailorService CreateService(HttpMessageHandler handler, string apiKey = "test_key")
    {
        var settings = Options.Create(new TicketVendorSettings
        {
            EventId = "ev_test",
            SyncIntervalMinutes = 15,
            ApiKey = apiKey
        });

        return new TicketTailorService(
            new HttpClient(handler),
            settings,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<TicketTailorService>.Instance);
    }
}

/// <summary>Replays queued responses in order and records every request it saw.</summary>
internal sealed class RecordingHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();

    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    public int RequestCount => Requests.Count;

    public void EnqueueResponse(HttpStatusCode status, object body) =>
        _responses.Enqueue(() => new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        });

    public void EnqueueThrow(Exception exception) =>
        _responses.Enqueue(() => throw exception);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request, body));
        return _responses.Dequeue()();
    }
}
