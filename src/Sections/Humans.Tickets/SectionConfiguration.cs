using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Humans.Tickets;

/// <summary><c>SectionJobs</c> registers the same key to read the sync interval at scheduling time.</summary>
internal sealed class SectionConfiguration : ISectionConfiguration
{
    public void DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        configuration.GetOptionalSetting(registry, "TicketVendor:EventId", "Ticket Vendor");
        configuration.GetOptionalSetting(registry, "TicketVendor:Provider", "Ticket Vendor");
        configuration.GetOptionalSetting(registry, "TicketVendor:SyncIntervalMinutes", "Ticket Vendor");

        registry.RegisterEnvironmentVariable("TICKET_VENDOR_API_KEY", "Ticket Vendor", isSensitive: true,
            importance: ConfigurationImportance.Recommended);
    }
}
