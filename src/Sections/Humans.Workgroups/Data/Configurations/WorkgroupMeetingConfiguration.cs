using Humans.Workgroups.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Workgroups.Data.Configurations;

internal sealed class WorkgroupMeetingConfiguration : IEntityTypeConfiguration<WorkgroupMeeting>
{
    public void Configure(EntityTypeBuilder<WorkgroupMeeting> b)
    {
        b.ToTable("workgroup_meetings");
        b.HasKey(m => m.Id);

        b.Property(m => m.WorkgroupId).IsRequired();
        b.Property(m => m.Title).HasMaxLength(200).IsRequired();
        b.Property(m => m.StartUtc).IsRequired();
        b.Property(m => m.EndUtc).IsRequired();
        b.Property(m => m.Location).HasMaxLength(500);
        b.Property(m => m.LocationUrl).HasMaxLength(2000);
        // Bool defaulting to false: IsRequired only — HasDefaultValue(false) is the sentinel trap.
        b.Property(m => m.IsPublic).IsRequired();
        b.Property(m => m.Minutes).HasColumnType("text");
        // CreatedByUserId is a bare cross-section reference: no FK, no navigation.
        b.Property(m => m.CreatedAt).IsRequired();
        b.Property(m => m.UpdatedAt).IsRequired();

        b.HasOne(m => m.Workgroup)
            .WithMany(w => w.Meetings)
            .HasForeignKey(m => m.WorkgroupId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(m => new { m.WorkgroupId, m.StartUtc });
        b.HasIndex(m => m.CreatedByUserId).HasFilter("\"CreatedByUserId\" IS NOT NULL");
    }
}
