using Humans.Backdoor.Data.Configurations;
using Humans.Backdoor.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Backdoor.Data;

internal sealed class BackdoorDbContext(DbContextOptions<BackdoorDbContext> options)
    : DbContext(options)
{
    public DbSet<BackdoorApiKey> ApiKeys => Set<BackdoorApiKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new BackdoorApiKeyConfiguration());
    }
}
