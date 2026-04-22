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

public class TicketTailorServiceTests
{
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

    [Fact]
    public async Task GetOrdersAsync_ParsesOrderResponse()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            data = new[]
            {
                new
                {
                    id = "ord_001",
                    buyer_details = new { first_name = "Jane", last_name = "Doe", email = "jane@example.com", name = "Jane Doe" },
                    total = 15000,
                    currency = new { code = "eur", base_multiplier = 100 },
                    status = "completed",
                    created_at = 1716811200L,
                    line_items = new[]
                    {
                        new { description = "Wave 1 Tickets", type = "ticket", total = 15000 },
                        new { description = "NCA Discount (NOBO25)", type = "gift_card", total = -2500 },
                    }
                }
            },
            links = new { next = (string?)null }
        });

        var service = CreateService(handler);
        var orders = await service.GetOrdersAsync(null, "ev_test");

        orders.Should().HaveCount(1);
        orders[0].BuyerName.Should().Be("Jane Doe");
        orders[0].TotalAmount.Should().Be(150m);
        orders[0].DiscountCode.Should().Be("NOBO25");
    }

    [Fact]
    public async Task GetOrdersAsync_HandlesPagination()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            data = new[]
            {
                new
                {
                    id = "ord_001",
                    buyer_details = new { first_name = "A", last_name = "B", email = "a@b.com", name = "A B" },
                    total = 100, currency = new { code = "eur", base_multiplier = 100 },
                    voucher_code = (string?)null, status = "completed", created_at = 1716811200L
                }
            },
            links = new { next = "has_more" }
        });
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            data = new[]
            {
                new
                {
                    id = "ord_002",
                    buyer_details = new { first_name = "C", last_name = "D", email = "c@d.com", name = "C D" },
                    total = 200, currency = new { code = "eur", base_multiplier = 100 },
                    voucher_code = (string?)null, status = "completed", created_at = 1716811200L
                }
            },
            links = new { next = (string?)null }
        });

        var service = CreateService(handler);
        var orders = await service.GetOrdersAsync(null, "ev_test");

        orders.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetOrdersAsync_ThrowsOnApiError()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.Unauthorized, new { error = "Invalid API key" });

        var service = CreateService(handler);
        var act = () => service.GetOrdersAsync(null, "ev_test");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GetIssuedTicketsAsync_ParsesTicketResponse()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            data = new[]
            {
                new
                {
                    id = "it_001",
                    first_name = "Jane",
                    last_name = "Doe",
                    full_name = "Jane Doe",
                    email = "jane@example.com",
                    description = "Full Week",
                    listed_price = 15000,
                    status = "valid",
                    order_id = "ord_001"
                }
            },
            links = new { next = (string?)null }
        });

        var service = CreateService(handler);
        var tickets = await service.GetIssuedTicketsAsync(null, "ev_test");

        tickets.Should().HaveCount(1);
        tickets[0].AttendeeName.Should().Be("Jane Doe");
        tickets[0].Price.Should().Be(150m);
        tickets[0].TicketTypeName.Should().Be("Full Week");
        tickets[0].VendorOrderId.Should().Be("ord_001");
    }

    [Fact]
    public async Task GetEventSummaryAsync_ParsesEventResponse()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            name = "Elsewhere 2026",
            total_holds = 0,
            total_issued_tickets = 96,
            total_orders = 88,
            ticket_types = new[]
            {
                new { quantity_total = 2000, quantity_issued = 86 },
                new { quantity_total = 500, quantity_issued = 6 },
                new { quantity_total = 500, quantity_issued = 4 },
            },
            ticket_groups = new[]
            {
                new { name = "Main tickets", max_quantity = 2000 },
            }
        });

        var service = CreateService(handler);
        var summary = await service.GetEventSummaryAsync("ev_test");

        summary.EventName.Should().Be("Elsewhere 2026");
        summary.TotalCapacity.Should().Be(2000);
        summary.TicketsSold.Should().Be(96);
        summary.TicketsRemaining.Should().Be(1904);
    }
}

/// <summary>Simple mock handler for testing HTTP responses.</summary>
public sealed class MockHttpHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();

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

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        return Task.FromResult(_responses.Dequeue());
    }
}
