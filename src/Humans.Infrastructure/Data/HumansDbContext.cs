using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data;

/// <summary>
/// Database context for the Humans application.
/// </summary>
/// <remarks>
/// <para>
/// Internal-sealed (issue #750). Access from outside this assembly is only
/// available via repository interfaces in <c>Humans.Application.Interfaces.Repositories</c>
/// or — for cross-cutting wiring — the extension methods in
/// <c>Humans.Infrastructure.Hosting.InfrastructureServiceCollectionExtensions</c>.
/// Test projects access this type via <c>InternalsVisibleTo</c>.
/// </para>
/// </remarks>
internal sealed class HumansDbContext(DbContextOptions<HumansDbContext> options)
    : IdentityDbContext<User, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<TeamJoinRequest> TeamJoinRequests => Set<TeamJoinRequest>();
    public DbSet<TeamJoinRequestStateHistory> TeamJoinRequestStateHistories => Set<TeamJoinRequestStateHistory>();
    public DbSet<TeamRoleDefinition> TeamRoleDefinitions => Set<TeamRoleDefinition>();
    public DbSet<TeamRoleAssignment> TeamRoleAssignments => Set<TeamRoleAssignment>();
    public DbSet<TeamEarlyEntryGrant> TeamEarlyEntryGrants => Set<TeamEarlyEntryGrant>();
    public DbSet<ContactField> ContactFields => Set<ContactField>();
    public DbSet<UserEmail> UserEmails => Set<UserEmail>();
    public DbSet<VolunteerHistoryEntry> VolunteerHistoryEntries => Set<VolunteerHistoryEntry>();
    public DbSet<EventSettings> EventSettings => Set<EventSettings>();
    public DbSet<Rota> Rotas => Set<Rota>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<ShiftSignup> ShiftSignups => Set<ShiftSignup>();
    public DbSet<VolunteerEventProfile> VolunteerEventProfiles => Set<VolunteerEventProfile>();
    public DbSet<GeneralAvailability> GeneralAvailability => Set<GeneralAvailability>();
    public DbSet<VolunteerBuildStatus> VolunteerBuildStatuses => Set<VolunteerBuildStatus>();
    public DbSet<AccountMergeRequest> AccountMergeRequests => Set<AccountMergeRequest>();
    public DbSet<CommunicationPreference> CommunicationPreferences => Set<CommunicationPreference>();
    public DbSet<ShiftTag> ShiftTags => Set<ShiftTag>();
    public DbSet<VolunteerTagPreference> VolunteerTagPreferences => Set<VolunteerTagPreference>();
    public DbSet<ProfileLanguage> ProfileLanguages => Set<ProfileLanguage>();
    public DbSet<EventParticipation> EventParticipations => Set<EventParticipation>();

    /// <summary>
    /// Configuration namespaces of sections peeled into their own DbContexts
    /// (nobodies-collective/Humans#858). Their tables are no longer part of this
    /// model; each peel appends its section here. Derived from a configuration
    /// type per section (not string literals) so a namespace move breaks the
    /// build instead of silently re-adding the section's tables to this model.
    /// A section that goes on to G5 (its own project, nobodies-collective/Humans#866)
    /// drops its entry again: ApplyConfigurationsFromAssembly scans this assembly, and
    /// the section's configurations are no longer in it.
    /// </summary>
    private static readonly string[] PeeledConfigurationNamespaces =
    [
        typeof(Configurations.Agent.AgentConversationConfiguration).Namespace!,
        typeof(Configurations.Auth.RoleAssignmentConfiguration).Namespace!,
        typeof(Configurations.Email.EmailOutboxMessageConfiguration).Namespace!,
        typeof(Configurations.Calendar.CalendarEventConfiguration).Namespace!,
        typeof(Configurations.Notifications.NotificationConfiguration).Namespace!,
        typeof(Configurations.Issues.IssueConfiguration).Namespace!,
        typeof(Configurations.Governance.ApplicationConfiguration).Namespace!,
        typeof(Configurations.Campaigns.CampaignConfiguration).Namespace!,
        typeof(Configurations.GoogleIntegration.GoogleResourceConfiguration).Namespace!,
        typeof(Configurations.Tickets.TicketOrderConfiguration).Namespace!,
        typeof(Configurations.Feedback.FeedbackReportConfiguration).Namespace!,
        typeof(Configurations.CityPlanning.CampPolygonConfiguration).Namespace!,
        typeof(Configurations.Budget.BudgetYearConfiguration).Namespace!,
        typeof(Configurations.Camps.CampConfiguration).Namespace!,
        typeof(Configurations.Gate.GateScanEventConfiguration).Namespace!,
        typeof(Configurations.Legal.LegalDocumentConfiguration).Namespace!,
        typeof(Configurations.AuditLog.AuditLogEntryConfiguration).Namespace!,
    ];

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Apply all configurations from the assembly, minus peeled sections'.
        builder.ApplyConfigurationsFromAssembly(
            typeof(HumansDbContext).Assembly,
            type => !PeeledConfigurationNamespaces.Contains(type.Namespace, StringComparer.Ordinal));

        // Rename Identity tables to use lowercase with underscores (PostgreSQL convention)
        builder.Entity<User>().ToTable("users");
        builder.Entity<IdentityRole<Guid>>().ToTable("roles");
        builder.Entity<IdentityUserRole<Guid>>().ToTable("user_roles");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");
    }
}
