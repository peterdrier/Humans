using System.Net;
using AwesomeAssertions;

namespace Humans.TicketTailor.Tests.Services;

public class TicketTailorServiceCachingTests
{
    [HumansFact]
    public async Task GetEventSummaryAsync_DoesNotCacheTransientServerFailures()
    {
        var handler = new RecordingHttpHandler();
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

        var service = TicketTailorTestHost.CreateService(handler);

        // 5xx throws — must not be cached so the second call can succeed
        var act = () => service.GetEventSummaryAsync("ev_test", Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<HttpRequestException>();

        var second = await service.GetEventSummaryAsync("ev_test", Xunit.TestContext.Current.CancellationToken);

        second.EventName.Should().Be("Elsewhere 2026");
        second.TotalCapacity.Should().Be(2000);
        second.TicketsSold.Should().Be(96);
        handler.RequestCount.Should().Be(2);
    }
}
