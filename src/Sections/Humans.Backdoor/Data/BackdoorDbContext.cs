using Humans.Backdoor.Data.Configurations;
using Humans.Backdoor.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Backdoor.Data;

internal sealed class BackdoorDbContext(DbContextOptions<BackdoorDbContext> options)
    : DbContext(options)
{
    public DbSet<BackdoorApiKey> ApiKeys => Set<BackdoorApiKey>();

    public DbSet<BackdoorOAuthConnection> OAuthConnections => Set<BackdoorOAuthConnection>();

    public DbSet<BackdoorOAuthToken> OAuthTokens => Set<BackdoorOAuthToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new BackdoorApiKeyConfiguration());
        builder.ApplyConfiguration(new BackdoorOAuthConnectionConfiguration());
        builder.ApplyConfiguration(new BackdoorOAuthTokenConfiguration());
    }
}
