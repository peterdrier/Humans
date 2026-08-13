using Humans.Application.Configuration;

namespace Humans.Web.Extensions.Infrastructure;

/// <summary>
/// Binds the ticket-vendor <em>port</em>'s settings. Which adapter implements the port is
/// <c>Humans.TicketTailor</c>'s <c>Section.Register</c>, discovered like any other section.
/// </summary>
/// <remarks>
/// The settings stay here rather than moving into the adapter because they belong to the
/// port, which lives in <c>Humans.Application</c>: <c>Humans.Tickets</c>' sync service reads
/// <c>EventId</c>, and Shell's <c>TicketVendorHealthCheck</c> reads <c>IsConfigured</c>.
/// Deleting the adapter for the 2027 vendor must not take the port's configuration with it
/// (Email's <c>EmailSettings</c> rule; G5-SECTION-TEMPLATE.md step 4).
/// </remarks>
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
