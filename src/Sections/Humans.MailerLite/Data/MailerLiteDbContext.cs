using Humans.MailerLite.Data.Configurations;
using Humans.MailerLite.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.MailerLite.Data;

internal sealed class MailerLiteDbContext(DbContextOptions<MailerLiteDbContext> options)
    : DbContext(options)
{
    public DbSet<MailerLiteSyncState> MailerLiteSyncStates => Set<MailerLiteSyncState>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new MailerLiteSyncStateConfiguration());
    }
}
