using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class AgentRateLimitConfiguration : IEntityTypeConfiguration<AgentRateLimit>
{
    public void Configure(EntityTypeBuilder<AgentRateLimit> builder)
    {
        builder.ToTable("agent_rate_limits");

        builder.HasKey(r => new { r.UserId, r.Day });

        builder.Property(r => r.MessagesToday).IsRequired();
        builder.Property(r => r.TokensToday).IsRequired();

        builder.HasOne(r => r.User)
            .WithMany()
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => r.Day);
    }
}
