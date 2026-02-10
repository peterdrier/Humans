using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Humans.Domain.Entities;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Infrastructure.Data;

/// <summary>
/// Database context for the Humans application.
/// </summary>
public class HumansDbContext : IdentityDbContext<User, IdentityRole<Guid>, Guid>
{
    public HumansDbContext(DbContextOptions<HumansDbContext> options)
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
    public DbSet<AuditLogEntry> AuditLogEntries => Set<AuditLogEntry>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Apply all configurations from the assembly
        builder.ApplyConfigurationsFromAssembly(typeof(HumansDbContext).Assembly);

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
