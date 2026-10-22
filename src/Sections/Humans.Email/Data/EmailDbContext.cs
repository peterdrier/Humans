using Humans.Email.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Email.Data;

internal sealed class EmailDbContext(DbContextOptions<EmailDbContext> options)
    : DbContext(options)
{
    public DbSet<EmailOutboxMessage> EmailOutboxMessages => Set<EmailOutboxMessage>();
    public DbSet<EmailDailySendCount> EmailDailySendCounts => Set<EmailDailySendCount>();
    public DbSet<CommunicationPreference> CommunicationPreferences => Set<CommunicationPreference>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new EmailOutboxMessageConfiguration());
        builder.ApplyConfiguration(new EmailDailySendCountConfiguration());
        builder.ApplyConfiguration(new CommunicationPreferenceConfiguration());
    }
}
