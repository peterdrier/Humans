using Humans.Shifts.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Shifts.Data.Configurations;

internal sealed class VolunteerTagPreferenceConfiguration : IEntityTypeConfiguration<VolunteerTagPreference>
{
    public void Configure(EntityTypeBuilder<VolunteerTagPreference> builder)
    {
        builder.ToTable("volunteer_tag_preferences");
        builder.HasKey(v => v.Id);

        builder.HasIndex(v => new { v.UserId, v.ShiftTagId })
            .IsUnique()
            .HasDatabaseName("IX_volunteer_tag_preferences_user_tag_unique");

        builder.HasIndex(v => v.UserId);

        // UserId is a bare cross-section Guid column — no FK constraint, no nav.

        builder.HasOne(v => v.ShiftTag)
            .WithMany(t => t.VolunteerPreferences)
            .HasForeignKey(v => v.ShiftTagId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
