using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Humans.Application.Configuration;
using Humans.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Humans.Application.Tests.Services;

public class TicketTailorServiceCachingTests
{
    [Fact]
    public async Task GetEventSummaryAsync_DoesNotCacheTransientServerFailures()
    {
        var handler = new CountingTicketTailorHandler();
        handler.EnqueueResponse(HttpStatusCode.InternalServerError, new { error = "temporary outage" });
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            name = "Elsewhere 2026",
            total_holds = 0,
            total_issued_tickets = 96,
            total_orders = 88,
            ticket_types = new[]
            {
                new { quantity_total = 2000, quantity_issued = 86 },
            },
            ticket_groups = new[]
            {
                new { name = "Main tickets", max_quantity = 2000 },
            }
        });

        var service = CreateService(handler);

        var first = await service.GetEventSummaryAsync("ev_test");
        var second = await service.GetEventSummaryAsync("ev_test");

        first.EventName.Should().Be("Unknown");
        first.TotalCapacity.Should().Be(0);

        second.EventName.Should().Be("Elsewhere 2026");
        second.TotalCapacity.Should().Be(2000);
        second.TicketsSold.Should().Be(96);
        handler.RequestCount.Should().Be(2);
    }

    private static TicketTailorService CreateService(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var settings = Options.Create(new TicketVendorSettings
        {
            EventId = "ev_test",
            SyncIntervalMinutes = 15,
            ApiKey = "test_key"
        });

        return new TicketTailorService(client, settings, cache,
            NullLogger<TicketTailorService>.Instance);
    }
}

internal sealed class CountingTicketTailorHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();

    public int RequestCount { get; private set; }

    public void EnqueueResponse(HttpStatusCode status, object body)
    {
        _responses.Enqueue(new HttpResponseMessage(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                System.Text.Encoding.UTF8,
                "application/json")
        });
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        return Task.FromResult(_responses.Dequeue());
    }
}
