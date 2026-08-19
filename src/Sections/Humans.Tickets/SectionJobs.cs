using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Humans.Tickets.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Tickets;

/// <summary>Tickets' recurring jobs. Discovered by Shell — nothing names it, so it needs no section prefix.</summary>
internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var registry = services.GetRequiredService<ConfigurationRegistry>();
        var syncIntervalMinutes = configuration.GetSettingValue(
            registry, "TicketVendor:SyncIntervalMinutes", "Ticket Vendor", defaultValue: 15);

        yield return new RecurringJobDescriptor(
            "tickets-vendor-sync", typeof(TicketSyncJob), $"*/{syncIntervalMinutes} * * * *");
    }
}
