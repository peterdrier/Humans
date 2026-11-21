using Humans.Roles.Domain;
using Humans.Teams.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Roles.Data.Configurations;

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("role_definitions");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.OwnerKind).HasMaxLength(50).IsRequired();
        builder.Property(r => r.Name).HasMaxLength(100).IsRequired();
        builder.Property(r => r.Slug).HasMaxLength(100).IsRequired();
        builder.Property(r => r.CustomSlug).HasMaxLength(100);
        builder.Property(r => r.Description).HasMaxLength(2000);
        builder.Property(r => r.Period).HasConversion<string>().HasMaxLength(50);

        builder.Property(r => r.Priorities)
            .IsRequired()
            .HasMaxLength(500)
            .HasConversion(
                v => string.Join(",", v.Select(p => p.ToString())),
                v => string.IsNullOrEmpty(v)
                    ? new List<SlotPriority>()
                    : v.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Enum.Parse<SlotPriority>).ToList(),
                new ValueComparer<List<SlotPriority>>(
                    (a, b) => a != null && b != null && a.SequenceEqual(b),
                    v => v.Aggregate(0, HashCode.Combine),
                    v => v.ToList()));

        builder.Property(r => r.PermissionKeys)
            .IsRequired()
            .HasMaxLength(1000)
            .HasConversion(
                v => string.Join(",", v),
                v => string.IsNullOrEmpty(v)
                    ? new List<string>()
                    : v.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
                new ValueComparer<List<string>>(
                    (a, b) => a != null && b != null && a.SequenceEqual(b),
                    v => v.Aggregate(0, (h, s) => HashCode.Combine(h, StringComparer.Ordinal.GetHashCode(s))),
                    v => v.ToList()));

        builder.HasIndex(r => new { r.OwnerKind, r.OwnerId, r.Slug }).IsUnique();
    }
}
