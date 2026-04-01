using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations;

public class CommunicationPreferenceConfiguration : IEntityTypeConfiguration<CommunicationPreference>
{
    public void Configure(EntityTypeBuilder<CommunicationPreference> builder)
    {
        builder.ToTable("communication_preferences");

        builder.HasKey(cp => cp.Id);

        builder.Property(cp => cp.Category)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(cp => cp.OptedOut)
            .IsRequired();

        builder.Property(cp => cp.InboxEnabled)
            .IsRequired()
            .HasDefaultValue(true)
            .HasSentinel(true);

        builder.Property(cp => cp.UpdatedAt)
            .IsRequired();

        builder.Property(cp => cp.UpdateSource)
            .HasMaxLength(100)
            .IsRequired();

        // One preference per user per category
        builder.HasIndex(cp => new { cp.UserId, cp.Category })
            .IsUnique();

        builder.HasIndex(cp => cp.UserId);
    }
}
