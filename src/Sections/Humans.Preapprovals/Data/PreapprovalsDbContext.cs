using Humans.Preapprovals.Data.Configurations;
using Humans.Preapprovals.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Preapprovals.Data;

/// <summary>
/// Per-section database context for Preapprovals: maps only <c>pre_programs</c>,
/// <c>pre_program_managers</c>, <c>pre_grants</c> and <c>pre_reminders_sent</c>, with its own
/// <c>__EFMigrationsHistory_Preapprovals</c> table and migrations under <c>Data/Migrations/</c>.
/// Same database, same connection — the split is a code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like every section context: the repository is the only consumer.
/// Configurations are applied explicitly so this model can never accrete another section's
/// tables. Cross-section references (<c>BeneficiaryUserId</c>, <c>UserId</c>, the actor ids,
/// <c>ExpenseReportId</c>) are bare Guid columns, never FKs.
/// </remarks>
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
