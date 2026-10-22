using Humans.Base.Interfaces;
using Humans.Tickets.Contracts;
using Humans.TicketTailor.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Humans.TicketTailor;

/// <summary>
/// <para>
/// Only an exactly-Production environment binds the HTTP client, whatever keys are set: a developer with a real
/// <c>TICKET_VENDOR_API_KEY</c> would otherwise write to the live ticketing account. <c>Register</c> has no
/// <c>IHostEnvironment</c>, so the name comes from configuration.
/// </para>
/// <para>
/// <c>TicketVendorSettings</c> is bound in Shell: it belongs to the Tickets port, and replacing this vendor must
/// not take it along.
/// </para>
/// </summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        var environment = configuration[HostDefaults.EnvironmentKey];

        if (string.Equals(environment, Environments.Production, StringComparison.Ordinal))
        {
            services.AddHttpClient<TicketTailorService>(client =>
            {
                // Production calls routinely take 21-27s.
                client.Timeout = TimeSpan.FromSeconds(90);
            });
            services.AddKeyedScoped<ITicketVendorService>(
                TicketVendorServiceKeys.InnerServiceKey,
                (sp, _) => sp.GetRequiredService<TicketTailorService>());
        }
        else
        {
            services.PostConfigure<TicketVendorSettings>(opts =>
            {
                if (string.IsNullOrEmpty(opts.EventId)) opts.EventId = "stub-event";
                if (string.IsNullOrEmpty(opts.ApiKey)) opts.ApiKey = "stub";
            });
            services.AddKeyedScoped<ITicketVendorService, StubTicketVendorService>(
                TicketVendorServiceKeys.InnerServiceKey);
        }
    }
}
