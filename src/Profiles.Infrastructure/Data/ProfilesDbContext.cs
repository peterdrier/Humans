using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Profiles.Domain.Entities;
using MemberApplication = Profiles.Domain.Entities.Application;

namespace Profiles.Infrastructure.Data;

/// <summary>
/// Database context for the Profiles application.
/// </summary>
public class ProfilesDbContext : IdentityDbContext<User, IdentityRole<Guid>, Guid>
{
    public ProfilesDbContext(DbContextOptions<ProfilesDbContext> options)
        : base(options)
    {
    }

    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();
    public DbSet<MemberApplication> Applications => Set<MemberApplication>();
    public DbSet<ApplicationStateHistory> ApplicationStateHistories => Set<ApplicationStateHistory>();
    public DbSet<LegalDocument> LegalDocuments => Set<LegalDocument>();
    public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();
    public DbSet<ConsentRecord> ConsentRecords => Set<ConsentRecord>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<TeamJoinRequest> TeamJoinRequests => Set<TeamJoinRequest>();
    public DbSet<TeamJoinRequestStateHistory> TeamJoinRequestStateHistories => Set<TeamJoinRequestStateHistory>();
    public DbSet<GoogleResource> GoogleResources => Set<GoogleResource>();
    public DbSet<ContactField> ContactFields => Set<ContactField>();
    public DbSet<VolunteerHistoryEntry> VolunteerHistoryEntries => Set<VolunteerHistoryEntry>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Apply all configurations from the assembly
        builder.ApplyConfigurationsFromAssembly(typeof(ProfilesDbContext).Assembly);

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
