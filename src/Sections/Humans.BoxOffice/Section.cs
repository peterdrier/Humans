using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Humans.BoxOffice.Services;
using Humans.Tickets.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.BoxOffice;

/// <summary>
/// Humans' own ticket checkout: Stripe hosted Checkout behind Tickets' <see cref="ITicketingPlatform"/>, key
/// <c>boxoffice</c>, on its own <c>STRIPE_BOXOFFICE_*</c> variables.
/// </summary>
public sealed class Section : ISection, ISectionConfiguration, ISectionAdminNav
{
    internal const string KeyVariable = "STRIPE_BOXOFFICE_KEY";
    internal const string WebhookSecretVariable = "STRIPE_BOXOFFICE_WEBHOOK_SECRET";

    public void Register(IServiceCollection services, IConfiguration configuration)
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
        registry.RegisterEnvironmentVariable(KeyVariable, "Box Office (Stripe)", isSensitive: true);
        registry.RegisterEnvironmentVariable(WebhookSecretVariable, "Box Office (Stripe)", isSensitive: true);
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
        [TicketsPlatformAdminNav.Group("Box Office", BoxOfficePlatform.PlatformKey, "fa-solid fa-cash-register")];
}
