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
    public DbSet<ContactField> ContactFields => Set<ContactField>();
    public DbSet<UserEmail> UserEmails => Set<UserEmail>();
    public DbSet<VolunteerHistoryEntry> VolunteerHistoryEntries => Set<VolunteerHistoryEntry>();
    public DbSet<AccountMergeRequest> AccountMergeRequests => Set<AccountMergeRequest>();
    public DbSet<CommunicationPreference> CommunicationPreferences => Set<CommunicationPreference>();
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
        typeof(Configurations.Auth.RoleAssignmentConfiguration).Namespace!,
        typeof(Configurations.GoogleIntegration.GoogleResourceConfiguration).Namespace!,
        typeof(Configurations.Tickets.TicketOrderConfiguration).Namespace!,
        typeof(Configurations.Camps.CampConfiguration).Namespace!,
        typeof(Configurations.AuditLog.AuditLogEntryConfiguration).Namespace!,
        typeof(Configurations.Shifts.RotaConfiguration).Namespace!,
        typeof(Configurations.Teams.TeamConfiguration).Namespace!,
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
