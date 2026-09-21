using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Humans.Tickets;

/// <summary>
/// Tickets' own settings — the vendor binding behind <c>TicketVendorSettings</c>. The
/// interval is also read at job-scheduling time by <c>SectionJobs</c>, which registers the
/// same key through the same extension.
/// </summary>
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
