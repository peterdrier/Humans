using Humans.Grants.Data.Configurations;
using Humans.Grants.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Grants.Data;

/// <summary>
/// Per-section database context for Grants: maps only <c>grant_programs</c>,
/// <c>grant_program_managers</c> and <c>grants</c>, with its own
/// <c>__EFMigrationsHistory_Grants</c> table and migrations under <c>Data/Migrations/</c>.
/// </summary>
/// <remarks>
/// Internal-sealed like every section context: the repository is the only consumer.
/// Cross-section references (<c>UserId</c> and the actor ids) are bare Guid columns, never FKs.
/// </remarks>
internal sealed class GrantsDbContext(DbContextOptions<GrantsDbContext> options) : DbContext(options)
{
    public DbSet<GrantProgram> Programs => Set<GrantProgram>();
    public DbSet<GrantProgramManager> Managers => Set<GrantProgramManager>();
    public DbSet<Grant> Grants => Set<Grant>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new GrantProgramConfiguration());
        builder.ApplyConfiguration(new GrantProgramManagerConfiguration());
        builder.ApplyConfiguration(new GrantConfiguration());
    }
}
