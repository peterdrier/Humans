using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Tickets.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Tickets;

/// <summary>
/// Tickets' health-check contribution, at the project root by convention. Discovered by
/// Shell — nothing names it, so it needs no section prefix. Keeps the "ticket-vendor"
/// monitoring key its existing name and "external" tag (nobodies-collective/Humans#1075).
/// </summary>
internal sealed class SectionHealthChecks : ISectionHealthChecks
{
    public void AddHealthChecks(IHealthChecksBuilder builder, IConfiguration configuration)
    {
        builder.AddCheck<TicketVendorHealthCheck>("ticket-vendor", tags: [HealthCheckTags.External]);
    }
}
