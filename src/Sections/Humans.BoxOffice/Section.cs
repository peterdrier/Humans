using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Humans.BoxOffice.Services;
using Humans.Tickets.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.BoxOffice;

/// <summary>
/// Humans' own ticketing platform: Stripe hosted Checkout behind Tickets' <see cref="ITicketingPlatform"/>, key
/// <c>boxoffice</c>, on the Tickets Stripe account with its own webhook signing secret.
/// </summary>
public sealed class Section : SectionBase, ISectionConfiguration, ISectionAdminNav
{
    internal const string KeyVariable = "STRIPE_TICKETS_KEY";
    internal const string WebhookSecretVariable = "STRIPE_BOXOFFICE_WEBHOOK_SECRET";

    public override void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BoxOfficeSettings>(opts =>
        {
            opts.SecretKey = Environment.GetEnvironmentVariable(KeyVariable) ?? string.Empty;
            opts.WebhookSecret = Environment.GetEnvironmentVariable(WebhookSecretVariable) ?? string.Empty;
        });
        services.AddScoped<BoxOfficePlatform>();
        services.AddScoped<ITicketingPlatform>(sp => sp.GetRequiredService<BoxOfficePlatform>());
    }

    void ISectionConfiguration.DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        registry.RegisterEnvironmentVariable(WebhookSecretVariable, "Box Office (Stripe)", isSensitive: true);
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
        [TicketsPlatformAdminNav.Group("Box Office", BoxOfficePlatform.PlatformKey, "fa-solid fa-cash-register")];
}
