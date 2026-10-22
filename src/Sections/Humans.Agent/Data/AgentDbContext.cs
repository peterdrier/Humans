using Humans.Agent.Data.Configurations;
using Microsoft.EntityFrameworkCore;
using Humans.Agent.Domain;

namespace Humans.Agent.Data;

internal sealed class AgentDbContext(DbContextOptions<AgentDbContext> options)
    : DbContext(options)
{
    public DbSet<AgentConversation> AgentConversations => Set<AgentConversation>();
    public DbSet<AgentMessage> AgentMessages => Set<AgentMessage>();
    public DbSet<AgentSettings> AgentSettings => Set<AgentSettings>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new AgentConversationConfiguration());
        builder.ApplyConfiguration(new AgentMessageConfiguration());
        builder.ApplyConfiguration(new AgentSettingsConfiguration());
    }
}
