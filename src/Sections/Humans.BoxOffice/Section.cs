using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Humans.BoxOffice.Services;
using Humans.Tickets.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.BoxOffice;

/// <summary>
/// Humans' own ticket checkout: Stripe hosted Checkout behind Tickets' <see cref="ITicketCheckoutBackend"/>, key
/// <c>boxoffice</c>. Declares its own <c>STRIPE_BOXOFFICE_*</c> variables; rename both places together.
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
        services.AddScoped<BoxOfficeCheckoutBackend>();
        services.AddScoped<ITicketCheckoutBackend>(sp => sp.GetRequiredService<BoxOfficeCheckoutBackend>());
    }

    void ISectionConfiguration.DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        registry.RegisterEnvironmentVariable(KeyVariable, "Box Office (Stripe)", isSensitive: true);
        registry.RegisterEnvironmentVariable(WebhookSecretVariable, "Box Office (Stripe)", isSensitive: true);
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
        [TicketsBackendAdminNav.Group("Box Office", BoxOfficeCheckoutBackend.BackendKey, "fa-solid fa-cash-register")];
}
