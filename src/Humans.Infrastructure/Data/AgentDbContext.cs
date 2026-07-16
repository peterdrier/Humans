using Humans.Domain.Entities;
using Humans.Infrastructure.Data.Configurations.Agent;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Data;

/// <summary>
/// Per-section database context for the Agent section
/// (nobodies-collective/Humans#858): maps only <c>agent_conversations</c>,
/// <c>agent_messages</c> and <c>agent_settings</c>, with its own
/// <c>__EFMigrationsHistory_Agent</c> table and migrations under
/// <c>Migrations/Agent/</c>. Same database, same connection — the split
/// is a code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like <see cref="HumansDbContext"/> (issue #750): repositories
/// are the only consumers. Configurations are applied explicitly (not by
/// assembly scanning) so this model can never accrete another section's tables.
/// </remarks>
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
