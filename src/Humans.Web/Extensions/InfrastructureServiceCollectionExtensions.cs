using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services;

namespace Humans.Web.Extensions;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddHumansInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.Configure<GitHubSettings>(configuration.GetSection(GitHubSettings.SectionName));
        services.Configure<EmailSettings>(configuration.GetSection(EmailSettings.SectionName));
        services.Configure<GoogleWorkspaceSettings>(configuration.GetSection(GoogleWorkspaceSettings.SectionName));
        services.Configure<TeamResourceManagementSettings>(configuration.GetSection(TeamResourceManagementSettings.SectionName));

        services.AddSingleton<HumansMetricsService>();

        services.AddScoped<ITeamService, TeamService>();
        services.AddScoped<IContactFieldService, ContactFieldService>();
        services.AddScoped<IUserEmailService, UserEmailService>();
        services.AddScoped<VolunteerHistoryService>();
        services.AddScoped<ILegalDocumentSyncService, LegalDocumentSyncService>();
        services.AddScoped<IAdminLegalDocumentService, AdminLegalDocumentService>();

        var googleWorkspaceConfig = configuration.GetSection(GoogleWorkspaceSettings.SectionName);
        var hasGoogleCredentials = !string.IsNullOrEmpty(googleWorkspaceConfig["ServiceAccountKeyPath"]) ||
                                   !string.IsNullOrEmpty(googleWorkspaceConfig["ServiceAccountKeyJson"]);

        if (hasGoogleCredentials)
        {
            services.AddScoped<IGoogleSyncService, GoogleWorkspaceSyncService>();
            services.AddScoped<ITeamResourceService, TeamResourceService>();
            services.AddScoped<IDriveActivityMonitorService, DriveActivityMonitorService>();
        }
        else if (environment.IsProduction())
        {
            throw new InvalidOperationException(
                "Google Workspace credentials are required in production. " +
                "Set GoogleWorkspace:ServiceAccountKeyPath or GoogleWorkspace:ServiceAccountKeyJson.");
        }
        else
        {
            services.AddScoped<IGoogleSyncService, StubGoogleSyncService>();
            services.AddScoped<ITeamResourceService, StubTeamResourceService>();
            services.AddScoped<IDriveActivityMonitorService, StubDriveActivityMonitorService>();
        }

        services.AddScoped<IEmailService, SmtpEmailService>();
        services.AddScoped<IMembershipCalculator, MembershipCalculator>();
        services.AddScoped<IRoleAssignmentService, RoleAssignmentService>();
        services.AddScoped<IAuditLogService, AuditLogService>();
        services.AddScoped<SystemTeamSyncJob>();
        services.AddScoped<SyncLegalDocumentsJob>();
        services.AddScoped<SendReConsentReminderJob>();
        services.AddScoped<ProcessAccountDeletionsJob>();
        services.AddScoped<SuspendNonCompliantMembersJob>();
        services.AddScoped<GoogleResourceReconciliationJob>();
        services.AddScoped<DriveActivityMonitorJob>();
        services.AddScoped<ProcessGoogleSyncOutboxJob>();

        return services;
    }
}
