using System.Security.Claims;
using AwesomeAssertions;
using Humans.Infrastructure.Logging;
using Humans.Testing;
using Microsoft.AspNetCore.Http;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace Humans.Application.Tests.Infrastructure.Logging;

public class CurrentUserEnricherTests
{
    [HumansFact]
    public void Enrich_AuthenticatedUser_AttachesUserId()
    {
        var userId = Guid.NewGuid();
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = BuildAuthenticatedPrincipal(userId)
            }
        };
        var enricher = new CurrentUserEnricher(accessor);
        var logEvent = CreateLogEvent();

        enricher.Enrich(logEvent, new SimplePropertyFactory());

        logEvent.Properties.Should().ContainKey("UserId");
        var prop = logEvent.Properties["UserId"];
        prop.Should().BeOfType<ScalarValue>();
        ((ScalarValue)prop).Value.Should().Be(userId);
    }

    [HumansFact]
    public void Enrich_AnonymousUser_AttachesNullUserId()
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity()) // no auth scheme = unauthenticated
            }
        };
        var enricher = new CurrentUserEnricher(accessor);
        var logEvent = CreateLogEvent();

        enricher.Enrich(logEvent, new SimplePropertyFactory());

        logEvent.Properties.Should().ContainKey("UserId");
        var prop = logEvent.Properties["UserId"];
        ((ScalarValue)prop).Value.Should().BeNull();
    }

    [HumansFact]
    public void Enrich_NoHttpContext_AttachesNullUserId()
    {
        // Simulates background jobs / startup emissions where no HttpContext is available.
        var enricher = new CurrentUserEnricher(httpContextAccessor: null);
        var logEvent = CreateLogEvent();

        enricher.Enrich(logEvent, new SimplePropertyFactory());

        logEvent.Properties.Should().ContainKey("UserId");
        var prop = logEvent.Properties["UserId"];
        ((ScalarValue)prop).Value.Should().BeNull();
    }

    [HumansFact]
    public void Enrich_AccessorWithNullHttpContext_AttachesNullUserId()
    {
        var enricher = new CurrentUserEnricher(new HttpContextAccessor { HttpContext = null });
        var logEvent = CreateLogEvent();

        enricher.Enrich(logEvent, new SimplePropertyFactory());

        ((ScalarValue)logEvent.Properties["UserId"]).Value.Should().BeNull();
    }

    [HumansFact]
    public void Enrich_NameIdentifierNotAGuid_AttachesNullUserId()
    {
        var identity = new ClaimsIdentity(authenticationType: "TestAuth");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "not-a-guid"));
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        var enricher = new CurrentUserEnricher(accessor);
        var logEvent = CreateLogEvent();

        enricher.Enrich(logEvent, new SimplePropertyFactory());

        ((ScalarValue)logEvent.Properties["UserId"]).Value.Should().BeNull();
    }

    private static ClaimsPrincipal BuildAuthenticatedPrincipal(Guid userId)
    {
        var identity = new ClaimsIdentity(authenticationType: "TestAuth");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, userId.ToString()));
        return new ClaimsPrincipal(identity);
    }

    private static LogEvent CreateLogEvent() => new(
        timestamp: DateTimeOffset.UtcNow,
        level: LogEventLevel.Information,
        exception: null,
        messageTemplate: new MessageTemplate("test", Array.Empty<MessageTemplateToken>()),
        properties: Array.Empty<LogEventProperty>());

    private sealed class SimplePropertyFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false)
            => new(name, new ScalarValue(value));
    }
}
