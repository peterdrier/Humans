using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Tickets.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Tickets;

internal sealed class SectionHealthChecks : ISectionHealthChecks
{
    public void AddHealthChecks(IHealthChecksBuilder builder, IConfiguration configuration)
    {
        builder.AddCheck<TicketVendorHealthCheck>("ticket-vendor", tags: [HealthCheckTags.External]);
    }
}
