using Humans.Governance.Domain;
using Humans.Governance.Data.Configurations;
using Microsoft.EntityFrameworkCore;
using MemberApplication = Humans.Governance.Domain.Application;

namespace Humans.Governance.Data;

/// <summary>
/// Per-section database context: maps only <c>applications</c>,
/// <c>application_state_history</c> and <c>board_votes</c>, with its own
/// <c>__EFMigrationsHistory_Governance</c> table and migrations under
/// <c>Data/Migrations/</c>. Same database, same connection — the split is a
/// code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Configurations are applied explicitly, not by assembly scanning, so this
/// model can never accrete another section's tables. Applicants and voting
/// Board members are bare Guid references, so the Users section's Identity
/// tables are deliberately absent here. These are Colaborador/Asociado tier
/// applications only — volunteer onboarding does not run through this section.
/// </remarks>
internal sealed class GovernanceDbContext(DbContextOptions<GovernanceDbContext> options)
    : DbContext(options)
{
    public DbSet<MemberApplication> Applications => Set<MemberApplication>();
    public DbSet<ApplicationStateHistory> ApplicationStateHistories => Set<ApplicationStateHistory>();
    public DbSet<BoardVote> BoardVotes => Set<BoardVote>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new ApplicationConfiguration());
        builder.ApplyConfiguration(new ApplicationStateHistoryConfiguration());
        builder.ApplyConfiguration(new BoardVoteConfiguration());
    }
}
