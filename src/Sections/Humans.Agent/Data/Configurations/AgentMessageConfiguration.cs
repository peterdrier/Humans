using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Agent.Domain;

namespace Humans.Agent.Data.Configurations;

internal sealed class AgentMessageConfiguration : IEntityTypeConfiguration<AgentMessage>
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
                v => System.Text.Json.JsonSerializer.Deserialize<string[]>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? Array.Empty<string>(),
                new ValueComparer<string[]>(
                    (a, b) => ReferenceEquals(a, b) || (a != null && b != null && a.SequenceEqual(b, StringComparer.Ordinal)),
                    v => v.Aggregate(0, (h, e) => HashCode.Combine(h, StringComparer.Ordinal.GetHashCode(e))),
                    v => v.ToArray()));

        builder.HasOne(m => m.Conversation)
            .WithMany(c => c.Messages)
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(m => m.ConversationId);
        builder.HasIndex(m => m.CreatedAt);
        builder.HasIndex(m => m.RefusalReason);
        builder.HasIndex(m => m.HandedOffToFeedbackId);
    }
}
