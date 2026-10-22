using Humans.Governance.Domain;
using Humans.Governance.Data.Configurations;
using Microsoft.EntityFrameworkCore;
using MemberApplication = Humans.Governance.Domain.Application;

namespace Humans.Governance.Data;

internal sealed class GovernanceDbContext(DbContextOptions<GovernanceDbContext> options)
    : DbContext(options)
{
    public DbSet<MemberApplication> Applications => Set<MemberApplication>();
    public DbSet<ApplicationStateHistory> ApplicationStateHistories => Set<ApplicationStateHistory>();
    public DbSet<BoardVote> BoardVotes => Set<BoardVote>();
    public DbSet<AssemblyVote> AssemblyVotes => Set<AssemblyVote>();
    public DbSet<AssemblyVoteOption> AssemblyVoteOptions => Set<AssemblyVoteOption>();
    public DbSet<AssemblyVoteRoster> AssemblyVoteRosterEntries => Set<AssemblyVoteRoster>();
    public DbSet<AssemblyBallot> AssemblyBallots => Set<AssemblyBallot>();
    public DbSet<AssemblyBallotHistory> AssemblyBallotHistories => Set<AssemblyBallotHistory>();
    public DbSet<AssemblyVotePeek> AssemblyVotePeeks => Set<AssemblyVotePeek>();
    public DbSet<MembershipTierRecord> MembershipTiers => Set<MembershipTierRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new ApplicationConfiguration());
        builder.ApplyConfiguration(new ApplicationStateHistoryConfiguration());
        builder.ApplyConfiguration(new BoardVoteConfiguration());
        builder.ApplyConfiguration(new AssemblyVoteConfiguration());
        builder.ApplyConfiguration(new AssemblyVoteOptionConfiguration());
        builder.ApplyConfiguration(new AssemblyVoteRosterConfiguration());
        builder.ApplyConfiguration(new AssemblyBallotConfiguration());
        builder.ApplyConfiguration(new AssemblyBallotHistoryConfiguration());
        builder.ApplyConfiguration(new AssemblyVotePeekConfiguration());
        builder.ApplyConfiguration(new MembershipTierRecordConfiguration());
    }
}
