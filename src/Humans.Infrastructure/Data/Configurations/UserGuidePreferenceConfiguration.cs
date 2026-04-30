using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class UserGuidePreferenceConfiguration : IEntityTypeConfiguration<UserGuidePreference>
{
    public void Configure(EntityTypeBuilder<UserGuidePreference> builder)
    {
        builder.ToTable("user_guide_preferences");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.ExcludedCategorySlugs).HasMaxLength(1000).IsRequired();

        builder.HasIndex(p => p.UserId).IsUnique();

        builder.HasOne(p => p.User)
            .WithOne()
            .HasForeignKey<UserGuidePreference>(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
