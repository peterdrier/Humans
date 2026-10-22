using Humans.Email.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Email.Data;

internal sealed class CommunicationPreferenceConfiguration : IEntityTypeConfiguration<CommunicationPreference>
{
    public void Configure(EntityTypeBuilder<CommunicationPreference> builder)
    {
        builder.ToTable("email_communication_preferences");

        builder.HasKey(cp => cp.Id);

        builder.Property(cp => cp.Category)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(cp => cp.InboxEnabled)
            .IsRequired();

        builder.Property(cp => cp.UpdatedAt)
            .IsRequired();

        builder.Property(cp => cp.UpdateSource)
            .HasMaxLength(100)
            .IsRequired();

        builder.HasIndex(cp => new { cp.UserId, cp.Category })
            .IsUnique();
    }
}
