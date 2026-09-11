using Humans.Base.Interfaces;
using Humans.Tickets.Contracts;
using Humans.TicketTailor.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Humans.TicketTailor;

/// <summary>
/// The TicketTailor adapter's DI entry point: binds exactly one implementation of
/// <see cref="ITicketVendorService"/>, the vendor-agnostic port owned by
/// <c>Humans.Tickets</c>.
/// </summary>
/// <remarks>
/// <para>
/// The environment name decides, never the presence of a key: only an exactly-Production
/// host environment binds the HTTP client; every other value, or none, binds the stub. A
/// developer holding a real <c>TICKET_VENDOR_API_KEY</c> still gets the stub, because pointing
/// them at the live vendor writes to a real ticketing account. <c>ISection.Register</c> has no
/// <c>IHostEnvironment</c>, so the name is read from the configuration it is handed
/// (<c>WebApplicationBuilder.Configuration</c> carries host configuration, and
/// <c>WebApplicationFactory.UseEnvironment</c> reaches it too).
/// </para>
/// <para>
/// The port's <c>IOptions&lt;TicketVendorSettings&gt;</c> binding stays in Shell: the settings
/// belong to the port (Tickets' sync service and <c>TicketVendorHealthCheck</c> read them), so
/// deleting this project for the 2027 vendor cannot take them with it.
/// </para>
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        var environment = configuration[HostDefaults.EnvironmentKey];

        if (string.Equals(environment, Environments.Production, StringComparison.Ordinal))
        {
            services.AddHttpClient<ITicketVendorService, TicketTailorService>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            });
            return;
        }

        services.PostConfigure<TicketVendorSettings>(opts =>
        {
            if (string.IsNullOrEmpty(opts.EventId)) opts.EventId = "stub-event";
            if (string.IsNullOrEmpty(opts.ApiKey)) opts.ApiKey = "stub";
        });
        services.AddScoped<ITicketVendorService, StubTicketVendorService>();
    }
}
