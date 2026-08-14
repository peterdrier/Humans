using Humans.Users.Contracts;
using Humans.Users.Data.Configurations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Humans.Users.Data;

/// <summary>
/// Per-section database context for the merged Users+Profiles section
/// (nobodies-collective/Humans#858, peel 15): maps <c>users</c>, <c>profiles</c>,
/// <c>profile_languages</c>, <c>contact_fields</c>, <c>user_emails</c>,
/// <c>volunteer_history_entries</c>, <c>communication_preferences</c>,
/// <c>account_merge_requests</c>, <c>event_participations</c> and the seven
/// ASP.NET Identity tables, with its own <c>__EFMigrationsHistory_Users</c>
/// table and migrations under <c>Data/Migrations/</c>. Same database, same
/// connection — the split is a code-side partition of the EF model.
/// </summary>
/// <remarks>
/// <para>
/// Profiles was mid-transition into Users and the move was never finished;
/// peel 15 accepts the merged state as the end state (design doc §10.3) —
/// one context, one repository family, no profiles↔users boundary.
/// </para>
/// <para>
/// Carries the Identity base (<c>IdentityDbContext</c>): nothing pins Identity
/// to a context name, so the base class moved here when <c>HumansDbContext</c>
/// was deleted. Internal-sealed like every section context (issue #750);
/// configurations are applied explicitly (not by assembly scanning) so this
/// model can never accrete another section's tables.
/// </para>
/// </remarks>
internal sealed class UsersDbContext(DbContextOptions<UsersDbContext> options)
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

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new UserConfiguration());
        builder.ApplyConfiguration(new AccountMergeRequestConfiguration());
        builder.ApplyConfiguration(new EventParticipationConfiguration());
        builder.ApplyConfiguration(new ProfileConfiguration());
        builder.ApplyConfiguration(new ProfileLanguageConfiguration());
        builder.ApplyConfiguration(new ContactFieldConfiguration());
        builder.ApplyConfiguration(new UserEmailConfiguration());
        builder.ApplyConfiguration(new VolunteerHistoryEntryConfiguration());
        builder.ApplyConfiguration(new CommunicationPreferenceConfiguration());

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
