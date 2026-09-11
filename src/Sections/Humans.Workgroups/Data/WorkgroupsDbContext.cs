using Humans.Workgroups.Data.Configurations;
using Humans.Workgroups.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Workgroups.Data;

/// <summary>
/// Per-section database context for Workgroups: maps only <c>workgroups</c>,
/// <c>workgroup_members</c>, <c>workgroup_meetings</c>, <c>workgroup_log_entries</c>,
/// <c>workgroup_documents</c> and <c>workgroup_document_comments</c>, with its own
/// <c>__EFMigrationsHistory_Workgroups</c> table and migrations under
/// <c>Data/Migrations/</c>. Same database, same connection — the split is a code-side
/// partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like every section context: the repository is the only consumer.
/// Configurations are applied explicitly (not by assembly scanning) so this model can
/// never accrete another section's tables. Cross-section references (<c>UserId</c>,
/// <c>AppliedByUserId</c>, <c>SurveyId</c>) are bare Guid columns, never FKs.
/// </remarks>
internal sealed class WorkgroupsDbContext(DbContextOptions<WorkgroupsDbContext> options)
    : DbContext(options)
{
    public DbSet<Workgroup> Workgroups => Set<Workgroup>();
    public DbSet<WorkgroupMember> Members => Set<WorkgroupMember>();
    public DbSet<WorkgroupMeeting> Meetings => Set<WorkgroupMeeting>();
    public DbSet<WorkgroupLogEntry> LogEntries => Set<WorkgroupLogEntry>();
    public DbSet<WorkgroupDocument> Documents => Set<WorkgroupDocument>();
    public DbSet<WorkgroupDocumentComment> Comments => Set<WorkgroupDocumentComment>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new WorkgroupConfiguration());
        builder.ApplyConfiguration(new WorkgroupMemberConfiguration());
        builder.ApplyConfiguration(new WorkgroupMeetingConfiguration());
        builder.ApplyConfiguration(new WorkgroupLogEntryConfiguration());
        builder.ApplyConfiguration(new WorkgroupDocumentConfiguration());
        builder.ApplyConfiguration(new WorkgroupDocumentCommentConfiguration());
    }
}
