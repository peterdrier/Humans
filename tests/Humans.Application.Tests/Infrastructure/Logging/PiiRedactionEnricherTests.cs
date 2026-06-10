using AwesomeAssertions;
using Humans.Infrastructure.Logging;
using Serilog.Core;
using Serilog.Events;

namespace Humans.Application.Tests.Infrastructure.Logging;

public class PiiRedactionEnricherTests
{
    private readonly PiiRedactionEnricher _enricher = new();

    [HumansFact]
    public void Enrich_ICalFeedRequestPath_RedactsTokenAndKeepsUserId()
    {
        var userId = Guid.NewGuid();
        var token = Guid.NewGuid();
        var logEvent = CreateLogEvent("RequestPath", $"/api/ical/{userId}/{token}.ics");

        _enricher.Enrich(logEvent, new SimplePropertyFactory());

        var value = ((ScalarValue)logEvent.Properties["RequestPath"]).Value;
        value.Should().Be($"/api/ical/{userId}/[redacted].ics");
        value.As<string>().Should().NotContain(token.ToString());
    }

    [HumansFact]
    public void Enrich_ICalFeedPathProperty_RedactsTokenToo()
    {
        var userId = Guid.NewGuid();
        var logEvent = CreateLogEvent("Path", $"/api/ical/{userId}/{Guid.NewGuid()}.ics");

        _enricher.Enrich(logEvent, new SimplePropertyFactory());

        ((ScalarValue)logEvent.Properties["Path"]).Value
            .Should().Be($"/api/ical/{userId}/[redacted].ics");
    }

    [HumansFact]
    public void Enrich_NonICalRequestPath_LeftUntouched()
    {
        var logEvent = CreateLogEvent("RequestPath", "/Shifts/Mine");

        _enricher.Enrich(logEvent, new SimplePropertyFactory());

        ((ScalarValue)logEvent.Properties["RequestPath"]).Value.Should().Be("/Shifts/Mine");
    }

    [HumansFact]
    public void Enrich_NonStringRequestPath_LeftUntouched()
    {
        var logEvent = CreateLogEvent("RequestPath", 42);

        _enricher.Enrich(logEvent, new SimplePropertyFactory());

        ((ScalarValue)logEvent.Properties["RequestPath"]).Value.Should().Be(42);
    }

    private static LogEvent CreateLogEvent(string propertyName, object? propertyValue) => new(
        timestamp: DateTimeOffset.UtcNow,
        level: LogEventLevel.Information,
        exception: null,
        messageTemplate: new MessageTemplate("test", []),
        properties: [new LogEventProperty(propertyName, new ScalarValue(propertyValue))]);

    private sealed class SimplePropertyFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false)
            => new(name, new ScalarValue(value));
    }
}
