using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Services;
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
        services.AddStripeInfrastructure(configuration);

        // Single key-addressed file storage rooted at wwwroot. Camps,
        // profile pictures, and any future file-bearing section share this
        // mount (production: Coolify volume at /app/wwwroot/uploads/).
        services.AddSingleton<IFileStorage, FileSystemFileStorage>();

        // Section-owned registrations. Each section file registers its own
        // repositories, services, jobs, options, and GDPR contributor forwarding.
        services.AddProfileSection(configuration);
        services.AddUsersSection();
        services.AddAuthSection();
        services.AddTeamsSection();
        services.AddGovernanceSection();
        services.AddOnboardingSection();
        services.AddCampsSection();
        services.AddCityPlanningSection(configuration);
        services.AddBudgetSection();
        services.AddShiftsSection();
        services.AddEarlyEntrySection();
        services.AddCalendarSection();
        services.AddTicketsSection();
        services.AddGateSection();
        services.AddFeedbackSection();
        services.AddIssuesSection();
        services.AddSurveySection();
        services.AddNotificationsSection();
        services.AddLegalAndConsentSection();
        services.AddCampaignsSection();
        services.AddAuditLogSection();
        services.AddGdprSection();
        services.AddICalFeedSection();
        services.AddAdminSection();
        services.AddGoogleIntegrationSection();
        services.AddGuideSection(configuration);
        services.AddAgentSection(configuration);
        services.AddSearchSection();
        services.AddHoldedConnector(configuration);
        services.AddMailerSection(configuration);
        services.AddExpensesSection(configuration);

        // Sections that have moved into their own project (nobodies-collective/Humans#866)
        // register themselves via ISection and are discovered, not named. The roll-call
        // above loses a line per section as the migration proceeds.
        services.AddDiscoveredSections(configuration);

        return services;
    }
}
