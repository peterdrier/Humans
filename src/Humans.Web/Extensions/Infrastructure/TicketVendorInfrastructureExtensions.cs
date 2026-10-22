using Humans.Tickets.Contracts;

namespace Humans.Web.Extensions.Infrastructure;

/// <summary>
/// Binds the ticket-vendor port's settings. They belong to the port in <c>Humans.Tickets.Contracts</c>, not the
/// adapter, so replacing the vendor adapter does not take the port's configuration with it.
/// </summary>
internal static class TicketVendorInfrastructureExtensions
{
    internal static IServiceCollection AddTicketVendorPort(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var ticketVendorApiKey = Environment.GetEnvironmentVariable("TICKET_VENDOR_API_KEY") ?? string.Empty;

        services.Configure<TicketVendorSettings>(opts =>
        {
            configuration.GetSection(TicketVendorSettings.SectionName).Bind(opts);
            opts.ApiKey = ticketVendorApiKey;
        });

        return services;
    }
}
