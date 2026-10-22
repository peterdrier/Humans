using Humans.Grants.Data.Configurations;
using Humans.Grants.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Grants.Data;

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
