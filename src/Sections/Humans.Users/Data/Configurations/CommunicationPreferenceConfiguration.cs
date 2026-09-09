using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Users.Contracts;
namespace Humans.Users.Data.Configurations;

internal sealed class CommunicationPreferenceConfiguration : IEntityTypeConfiguration<CommunicationPreference>
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

        builder.Property(x => x.SubscribedAt)
            .HasColumnName("SubscribedAt")
            .HasColumnType("timestamp with time zone");

        // This config owns the cascade-delete FK from User to CommunicationPreference.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(cp => cp.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(cp => new { cp.UserId, cp.Category })
            .IsUnique();

        builder.HasIndex(cp => cp.UserId);
    }
}
