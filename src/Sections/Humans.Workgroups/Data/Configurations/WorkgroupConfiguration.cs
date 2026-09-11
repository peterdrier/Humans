using Humans.Workgroups.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Workgroups.Data.Configurations;

internal sealed class WorkgroupConfiguration : IEntityTypeConfiguration<Workgroup>
{
    public void Configure(EntityTypeBuilder<Workgroup> b)
    {
        b.ToTable("workgroups");
        b.HasKey(w => w.Id);

        b.Property(w => w.Name).HasMaxLength(200).IsRequired();
        b.Property(w => w.Slug).HasMaxLength(100).IsRequired();
        b.Property(w => w.Purpose).HasMaxLength(4000).IsRequired();
        b.Property(w => w.Deliverable).HasMaxLength(500).IsRequired();
        b.Property(w => w.DeliverableKind).IsRequired().HasConversion<string>().HasMaxLength(50);
        b.Property(w => w.Audience).IsRequired().HasConversion<string>().HasMaxLength(50);
        b.Property(w => w.Status).IsRequired().HasConversion<string>().HasMaxLength(50);
        b.Property(w => w.DormantReason).HasConversion<string>().HasMaxLength(50);
        b.Property(w => w.DriveFolderId).HasMaxLength(100);
        b.Property(w => w.DiscordChannelUrl).HasMaxLength(500);
        b.Property(w => w.Reasons).HasMaxLength(4000);
        // AppliedByUserId is a bare cross-section reference: no FK, no navigation.
        b.Property(w => w.AppliedAt).IsRequired();
        b.Property(w => w.CreatedAt).IsRequired();
        b.Property(w => w.UpdatedAt).IsRequired();

        b.HasIndex(w => w.Slug).IsUnique();
        b.HasIndex(w => w.Status);
        // One group per folder: a second claim on the same folder would make two sections'
        // expected access fight each other in the Drive fan-out.
        b.HasIndex(w => w.DriveFolderId).IsUnique().HasFilter("\"DriveFolderId\" IS NOT NULL");
    }
}
