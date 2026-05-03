using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class AgentMessageConfiguration : IEntityTypeConfiguration<AgentMessage>
{
    public void Configure(EntityTypeBuilder<AgentMessage> builder)
    {
        builder.ToTable("agent_messages");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Role).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(m => m.Content).IsRequired(); // text (unbounded) — transcripts can be long
        builder.Property(m => m.CreatedAt).IsRequired();
        builder.Property(m => m.Model).HasMaxLength(64).IsRequired();
        builder.Property(m => m.RefusalReason).HasMaxLength(256);

        builder.Property(m => m.FetchedDocs)
            .HasColumnType("jsonb")
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => System.Text.Json.JsonSerializer.Deserialize<string[]>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? Array.Empty<string>());

        // Same-section FK only: agent_messages → agent_conversations.
        builder.HasOne(m => m.Conversation)
            .WithMany(c => c.Messages)
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        // No cross-section FK to FeedbackReport. HandedOffToFeedbackId is a
        // plain nullable Guid column. Feedback handoff lookup goes through
        // IFeedbackService when display data is needed.
        builder.HasIndex(m => m.ConversationId);
        builder.HasIndex(m => m.CreatedAt);
        builder.HasIndex(m => m.RefusalReason);
        builder.HasIndex(m => m.HandedOffToFeedbackId);
    }
}
