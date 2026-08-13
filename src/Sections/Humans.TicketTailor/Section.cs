using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.TicketVendor;
using Humans.TicketTailor.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Humans.TicketTailor;

/// <summary>
/// The TicketTailor adapter's DI entry point: binds exactly one implementation of
/// <see cref="ITicketVendorService"/>, the vendor-agnostic port that stays in
/// <c>Humans.Application</c>.
/// </summary>
/// <remarks>
/// <para>
/// The port's <c>IOptions&lt;TicketVendorSettings&gt;</c> binding is <em>not</em> here: the
/// settings belong to the port, not to this adapter — <c>Humans.Tickets</c>' sync service
/// and Shell's <c>TicketVendorHealthCheck</c> both read them — so Shell keeps that line
/// (Governance's rule: the section that owns the file is not always the section that owns
/// the line). Deleting this project in 2027 must not take the port's configuration with it.
/// </para>
/// <para>
/// <c>ISection.Register</c> has no <c>IHostEnvironment</c>, so the Production test that used
/// to live in Shell reads the environment out of the configuration it <em>is</em> handed —
/// <c>WebApplicationBuilder.Configuration</c> includes host configuration, and
/// <c>WebApplicationFactory.UseEnvironment</c> reaches it too (Development's rule). The check
/// is written to fail closed: anything other than a present, exactly-Production environment
/// name gets the stub. Branching on whether the API key is <em>configured</em> instead would
/// have been the cheaper-looking copy of Email's shape and a behaviour change — today a
/// developer holding a real <c>TICKET_VENDOR_API_KEY</c> still gets the stub, and pointing
/// them at the live vendor writes to a real ticketing account.
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
