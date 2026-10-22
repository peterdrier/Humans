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
        b.Property(w => w.BudgetAmount).HasPrecision(18, 2);
        b.Property(w => w.HoldedAccountId).HasMaxLength(64);
        b.Property(w => w.DiscordChannelUrl).HasMaxLength(500);
        b.Property(w => w.Reasons).HasMaxLength(4000);
        b.Property(w => w.AppliedAt).IsRequired();
        b.Property(w => w.CreatedAt).IsRequired();
        b.Property(w => w.UpdatedAt).IsRequired();

        // Plain, not unique: no unique constraints on user-editable strings. ReserveSlugAsync resolves collisions.
        b.HasIndex(w => w.Slug);
        b.HasIndex(w => w.Status);
        // One group per folder: two claims on one folder would make their expected access fight in the Drive fan-out.
        b.HasIndex(w => w.DriveFolderId).IsUnique().HasFilter("\"DriveFolderId\" IS NOT NULL");
    }
}
