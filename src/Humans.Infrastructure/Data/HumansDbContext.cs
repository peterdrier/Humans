using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Humans.Domain.Entities;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Infrastructure.Data;

/// <summary>
/// Database context for the Humans application.
/// </summary>
public class HumansDbContext : IdentityDbContext<User, IdentityRole<Guid>, Guid>, IDataProtectionKeyContext
{
    public HumansDbContext(DbContextOptions<HumansDbContext> options)
        : base(options)
    {
    }

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

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
    public DbSet<TeamRoleDefinition> TeamRoleDefinitions => Set<TeamRoleDefinition>();
    public DbSet<TeamRoleAssignment> TeamRoleAssignments => Set<TeamRoleAssignment>();
    public DbSet<GoogleResource> GoogleResources => Set<GoogleResource>();
    public DbSet<GoogleSyncOutboxEvent> GoogleSyncOutboxEvents => Set<GoogleSyncOutboxEvent>();
    public DbSet<ContactField> ContactFields => Set<ContactField>();
    public DbSet<UserEmail> UserEmails => Set<UserEmail>();
    public DbSet<VolunteerHistoryEntry> VolunteerHistoryEntries => Set<VolunteerHistoryEntry>();
    public DbSet<AuditLogEntry> AuditLogEntries => Set<AuditLogEntry>();
    public DbSet<BoardVote> BoardVotes => Set<BoardVote>();
    public DbSet<SyncServiceSettings> SyncServiceSettings => Set<SyncServiceSettings>();
    public DbSet<Camp> Camps => Set<Camp>();
    public DbSet<CampSeason> CampSeasons => Set<CampSeason>();
    public DbSet<CampLead> CampLeads => Set<CampLead>();
    public DbSet<CampHistoricalName> CampHistoricalNames => Set<CampHistoricalName>();
    public DbSet<CampImage> CampImages => Set<CampImage>();
    public DbSet<CampSettings> CampSettings => Set<CampSettings>();
    public DbSet<EmailOutboxMessage> EmailOutboxMessages { get; set; } = null!;
    public DbSet<Campaign> Campaigns { get; set; } = null!;
    public DbSet<CampaignCode> CampaignCodes { get; set; } = null!;
    public DbSet<CampaignGrant> CampaignGrants { get; set; } = null!;
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<TicketOrder> TicketOrders => Set<TicketOrder>();
    public DbSet<TicketAttendee> TicketAttendees => Set<TicketAttendee>();
    public DbSet<TicketSyncState> TicketSyncStates => Set<TicketSyncState>();
    public DbSet<EventSettings> EventSettings => Set<EventSettings>();
    public DbSet<Rota> Rotas => Set<Rota>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<ShiftSignup> ShiftSignups => Set<ShiftSignup>();
    public DbSet<VolunteerEventProfile> VolunteerEventProfiles => Set<VolunteerEventProfile>();
    public DbSet<GeneralAvailability> GeneralAvailability => Set<GeneralAvailability>();
    public DbSet<FeedbackReport> FeedbackReports => Set<FeedbackReport>();
    public DbSet<FeedbackMessage> FeedbackMessages => Set<FeedbackMessage>();
    public DbSet<AccountMergeRequest> AccountMergeRequests => Set<AccountMergeRequest>();
    public DbSet<CommunicationPreference> CommunicationPreferences => Set<CommunicationPreference>();
    public DbSet<BudgetYear> BudgetYears => Set<BudgetYear>();
    public DbSet<BudgetGroup> BudgetGroups => Set<BudgetGroup>();
    public DbSet<BudgetCategory> BudgetCategories => Set<BudgetCategory>();
    public DbSet<BudgetLineItem> BudgetLineItems => Set<BudgetLineItem>();
    public DbSet<BudgetAuditLog> BudgetAuditLogs => Set<BudgetAuditLog>();

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
