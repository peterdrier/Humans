using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Humans.Infrastructure.Services;
using Humans.Testing;
using Xunit;

namespace Humans.Web.Tests.Services;

public class HttpStatusTrackerTests
{
    [HumansFact]
    public async Task TalliesByStatusCode()
    {
        var tracker = new HttpStatusTracker();
        await tracker.StartAsync(CancellationToken.None);

        using (var meter = new Meter("Microsoft.AspNetCore.Hosting"))
        {
            var hist = meter.CreateHistogram<double>("http.server.request.duration");
            Record(hist, 200);
            Record(hist, 200);
            Record(hist, 200);
            Record(hist, 404);
            Record(hist, 500);

            var counts = tracker.GetCounts();
            counts[200].Should().Be(3);
            counts[404].Should().Be(1);
            counts[500].Should().Be(1);
            tracker.Total.Should().Be(5);
        }

        await tracker.StopAsync(CancellationToken.None);

        static void Record(Histogram<double> h, int statusCode)
            => h.Record(0.01, new KeyValuePair<string, object?>("http.response.status_code", statusCode));
    }

    [HumansFact]
    public async Task IgnoresMeasurementsWithoutStatusCodeTag()
    {
        var tracker = new HttpStatusTracker();
        await tracker.StartAsync(CancellationToken.None);

        using (var meter = new Meter("Microsoft.AspNetCore.Hosting"))
        {
            var hist = meter.CreateHistogram<double>("http.server.request.duration");
            hist.Record(0.01); // no tags

            tracker.Total.Should().Be(0);
        }

        await tracker.StopAsync(CancellationToken.None);
    }

    [HumansFact]
    public async Task IgnoresInstrumentsOnOtherMeters()
    {
        var tracker = new HttpStatusTracker();
        await tracker.StartAsync(CancellationToken.None);

        using (var meter = new Meter("Some.Other.Meter"))
        {
            var hist = meter.CreateHistogram<double>("http.server.request.duration");
            hist.Record(0.01, new KeyValuePair<string, object?>("http.response.status_code", 200));

            tracker.Total.Should().Be(0);
        }

        await tracker.StopAsync(CancellationToken.None);
    }
}
