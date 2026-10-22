using Humans.Workgroups.Data.Configurations;
using Humans.Workgroups.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Workgroups.Data;

internal sealed class WorkgroupsDbContext(DbContextOptions<WorkgroupsDbContext> options)
    : DbContext(options)
{
    public DbSet<WorkgroupsSettings> Settings => Set<WorkgroupsSettings>();
    public DbSet<Workgroup> Workgroups => Set<Workgroup>();
    public DbSet<WorkgroupMember> Members => Set<WorkgroupMember>();
    public DbSet<WorkgroupMeeting> Meetings => Set<WorkgroupMeeting>();
    public DbSet<WorkgroupLogEntry> LogEntries => Set<WorkgroupLogEntry>();
    public DbSet<WorkgroupDocument> Documents => Set<WorkgroupDocument>();
    public DbSet<WorkgroupDocumentComment> Comments => Set<WorkgroupDocumentComment>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new WorkgroupsSettingsConfiguration());
        builder.ApplyConfiguration(new WorkgroupConfiguration());
        builder.ApplyConfiguration(new WorkgroupMemberConfiguration());
        builder.ApplyConfiguration(new WorkgroupMeetingConfiguration());
        builder.ApplyConfiguration(new WorkgroupLogEntryConfiguration());
        builder.ApplyConfiguration(new WorkgroupDocumentConfiguration());
        builder.ApplyConfiguration(new WorkgroupDocumentCommentConfiguration());
    }
}
