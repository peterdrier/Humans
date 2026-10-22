using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Agent.Domain;

namespace Humans.Agent.Data.Configurations;

internal sealed class AgentConversationConfiguration : IEntityTypeConfiguration<AgentConversation>
{
    public void Configure(EntityTypeBuilder<AgentConversation> builder)
    {
        builder.ToTable("agent_conversations");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Locale).HasMaxLength(16).IsRequired();
        builder.Property(c => c.StartedAt).IsRequired();
        builder.Property(c => c.LastMessageAt).IsRequired();
        builder.Property(c => c.MessageCount).IsRequired();

        builder.HasIndex(c => c.UserId);
        builder.HasIndex(c => c.LastMessageAt);
    }
}
