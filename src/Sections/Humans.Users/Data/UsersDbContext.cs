using Humans.Users.Contracts;
using Humans.Users.Data.Configurations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Humans.Users.Data;

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

        builder.Entity<User>().ToTable("users");
        builder.Entity<IdentityRole<Guid>>().ToTable("roles");
        builder.Entity<IdentityUserRole<Guid>>().ToTable("user_roles");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");
    }
}
