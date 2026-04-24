using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Shifts;

public class VolunteerTagPreferenceConfiguration : IEntityTypeConfiguration<VolunteerTagPreference>
{
    public void Configure(EntityTypeBuilder<VolunteerTagPreference> builder)
    {
        builder.ToTable("volunteer_tag_preferences");
        builder.HasKey(v => v.Id);

        builder.HasIndex(v => new { v.UserId, v.ShiftTagId })
            .IsUnique()
            .HasDatabaseName("IX_volunteer_tag_preferences_user_tag_unique");

        builder.HasIndex(v => v.UserId);

        // EF needs the nav ref to configure the cross-section FK + cascade.
        // The nav is [Obsolete] for the Application layer (design-rules §6c).
#pragma warning disable CS0618
        builder.HasOne(v => v.User)
            .WithMany()
            .HasForeignKey(v => v.UserId)
            .OnDelete(DeleteBehavior.Cascade);
#pragma warning restore CS0618

        builder.HasOne(v => v.ShiftTag)
            .WithMany(t => t.VolunteerPreferences)
            .HasForeignKey(v => v.ShiftTagId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
