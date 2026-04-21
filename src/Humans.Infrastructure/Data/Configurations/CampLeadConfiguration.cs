using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class CampLeadConfiguration : IEntityTypeConfiguration<CampLead>
{
    public void Configure(EntityTypeBuilder<CampLead> builder)
    {
        builder.ToTable("camp_leads");

        builder.Property(l => l.Role).HasConversion<string>().HasMaxLength(50).IsRequired();

        builder.HasIndex(l => new { l.CampId, l.UserId })
            .HasFilter("\"LeftAt\" IS NULL")
            .IsUnique()
            .HasDatabaseName("IX_camp_leads_active_unique");

        // Uni-directional relationship — CampLead.User nav stripped per §6.
        // The FK column (camp_leads.UserId → AspNetUsers.Id) remains; callers
        // resolve the user via IUserService.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(l => l.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Ignore(l => l.IsActive);
    }
}
