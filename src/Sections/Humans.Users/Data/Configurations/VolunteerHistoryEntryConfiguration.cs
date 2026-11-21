using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Users.Contracts;

namespace Humans.Users.Data.Configurations;

internal sealed class VolunteerHistoryEntryConfiguration : IEntityTypeConfiguration<VolunteerHistoryEntry>
{
    public void Configure(EntityTypeBuilder<VolunteerHistoryEntry> builder)
    {
        builder.ToTable("user_volunteer_history_entries");

        builder.HasKey(v => v.Id);

        builder.Property(v => v.Date)
            .IsRequired();

        builder.Property(v => v.EventName)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(v => v.Description)
            .HasMaxLength(2000);

        builder.Property(v => v.CreatedAt)
            .IsRequired();

        builder.Property(v => v.UpdatedAt)
            .IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(v => v.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Declared so the model matches the default EF's nullable-to-required migration leaves in the
        // database (required-columns-need-approval). Every write sets a real user id.
        builder.Property(v => v.UserId).HasDefaultValue(Guid.Empty);

        builder.HasIndex(v => v.UserId);
    }
}
