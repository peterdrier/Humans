using Humans.Workgroups.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Workgroups.Data.Configurations;

internal sealed class WorkgroupMemberConfiguration : IEntityTypeConfiguration<WorkgroupMember>
{
    public void Configure(EntityTypeBuilder<WorkgroupMember> b)
    {
        b.ToTable("workgroup_members");
        b.HasKey(m => m.Id);

        b.Property(m => m.WorkgroupId).IsRequired();
        // UserId is a bare cross-section reference: indexed for the per-user reads, no FK.
        b.Property(m => m.UserId).IsRequired();
        b.Property(m => m.Role).IsRequired().HasConversion<string>().HasMaxLength(50);
        b.Property(m => m.JoinedAt).IsRequired();

        b.HasOne(m => m.Workgroup)
            .WithMany(w => w.Members)
            .HasForeignKey(m => m.WorkgroupId)
            .OnDelete(DeleteBehavior.Cascade);

        // One current membership per person per group; a rejoin after leaving is a new row.
        b.HasIndex(m => new { m.WorkgroupId, m.UserId }).IsUnique().HasFilter("\"LeftAt\" IS NULL");
        b.HasIndex(m => m.UserId);
    }
}
