using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Services;

namespace Humans.Web.Extensions.Infrastructure;

internal static class TicketVendorInfrastructureExtensions
{
    internal static IServiceCollection AddTicketVendorInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        // Ticket vendor integration
        var ticketVendorApiKey = Environment.GetEnvironmentVariable("TICKET_VENDOR_API_KEY") ?? string.Empty;

        services.Configure<TicketVendorSettings>(opts =>
        {
            configuration.GetSection(TicketVendorSettings.SectionName).Bind(opts);
            opts.ApiKey = ticketVendorApiKey;
        });

        if (environment.IsProduction())
        {
            services.AddHttpClient<ITicketVendorService, TicketTailorService>();
        }
        else
        {
            // Stub is self-contained — fill in defaults so IsConfigured passes
            services.PostConfigure<TicketVendorSettings>(opts =>
            {
                if (string.IsNullOrEmpty(opts.EventId)) opts.EventId = "stub-event";
                if (string.IsNullOrEmpty(opts.ApiKey)) opts.ApiKey = "stub";
            });
            services.AddScoped<ITicketVendorService, StubTicketVendorService>();
        }

        return services;
    }
}
