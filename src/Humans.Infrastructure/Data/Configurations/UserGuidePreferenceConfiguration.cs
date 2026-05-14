using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class UserGuidePreferenceConfiguration : IEntityTypeConfiguration<EventPreference>
{
    public void Configure(EntityTypeBuilder<EventPreference> builder)
    {
        builder.ToTable("user_guide_preferences");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.ExcludedCategorySlugs).HasMaxLength(1000).IsRequired();

        builder.HasIndex(p => p.UserId).IsUnique();

        builder.HasOne<User>()
            .WithOne()
            .HasForeignKey<EventPreference>(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
