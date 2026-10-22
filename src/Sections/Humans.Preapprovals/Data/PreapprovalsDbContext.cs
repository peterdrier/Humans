using Humans.Preapprovals.Data.Configurations;
using Humans.Preapprovals.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Preapprovals.Data;

internal sealed class PreapprovalsDbContext(DbContextOptions<PreapprovalsDbContext> options)
    : DbContext(options)
{
    public DbSet<PreapprovalProgram> Programs => Set<PreapprovalProgram>();
    public DbSet<ProgramManager> Managers => Set<ProgramManager>();
    public DbSet<PreapprovalGrant> Grants => Set<PreapprovalGrant>();
    public DbSet<ReminderSent> RemindersSent => Set<ReminderSent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new PreapprovalProgramConfiguration());
        builder.ApplyConfiguration(new ProgramManagerConfiguration());
        builder.ApplyConfiguration(new PreapprovalGrantConfiguration());
        builder.ApplyConfiguration(new ReminderSentConfiguration());
    }
}
