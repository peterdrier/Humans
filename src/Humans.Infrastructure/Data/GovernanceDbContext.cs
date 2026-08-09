using Humans.Domain.Entities;
using Humans.Infrastructure.Data.Configurations.Governance;
using Microsoft.EntityFrameworkCore;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Infrastructure.Data;

/// <summary>
/// Per-section database context for the Governance section
/// (nobodies-collective/Humans#858): maps only <c>applications</c>,
/// <c>application_state_history</c> and <c>board_votes</c>, with its own
/// <c>__EFMigrationsHistory_Governance</c> table and migrations under
/// <c>Migrations/Governance/</c>. Same database, same connection — the split is
/// a code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like <see cref="HumansDbContext"/> (issue #750): repositories
/// are the only consumers. Configurations are applied explicitly (not by
/// assembly scanning) so this model can never accrete another section's tables.
/// Applicants and voting Board members are bare Guid references, so the
/// Identity tables stay in <see cref="HumansDbContext"/> and are deliberately
/// absent here. These are Colaborador/Asociado tier applications only —
/// volunteer onboarding does not run through this section.
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
