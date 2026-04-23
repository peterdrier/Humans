using Humans.Application.Configuration;
using Humans.Web.Extensions.Infrastructure;
using Humans.Web.Extensions.Sections;

namespace Humans.Web.Extensions;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddHumansInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        ConfigurationRegistry? configRegistry = null)
    {
        // Cross-cutting infrastructure — options bindings, integrations, config metadata.
        services.AddConfigurationMetadata(configuration, configRegistry);
        services.AddTelemetryInfrastructure(configuration);
        services.AddEmailInfrastructure(configuration, environment);
        services.AddGoogleWorkspaceInfrastructure(configuration, environment);
        services.AddTicketVendorInfrastructure(configuration, environment);
        services.AddStripeInfrastructure();

        // Section-owned registrations. Each section file registers its own
        // repositories, services, jobs, options, and GDPR contributor forwarding.
        services.AddProfileSection();
        services.AddUsersSection();
        services.AddAuthSection();
        services.AddTeamsSection();
        services.AddGovernanceSection();
        services.AddCampsSection();
        services.AddCityPlanningSection(configuration);
        services.AddBudgetSection();
        services.AddShiftsSection();
        services.AddCalendarSection();
        services.AddTicketsSection();
        services.AddFeedbackSection();
        services.AddNotificationsSection();
        services.AddLegalAndConsentSection();
        services.AddCampaignsSection();
        services.AddAuditLogSection();
        services.AddGdprSection();
        services.AddAdminSection();

        return services;
    }
}
