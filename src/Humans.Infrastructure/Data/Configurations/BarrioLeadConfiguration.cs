using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class BarrioLeadConfiguration : IEntityTypeConfiguration<BarrioLead>
{
    public void Configure(EntityTypeBuilder<BarrioLead> builder)
    {
        builder.ToTable("barrio_leads");

        builder.Property(l => l.Role).HasConversion<string>().HasMaxLength(50).IsRequired();

        builder.HasIndex(l => new { l.BarrioId, l.UserId })
            .HasFilter("\"LeftAt\" IS NULL")
            .IsUnique()
            .HasDatabaseName("IX_barrio_leads_active_unique");

        builder.HasOne(l => l.User)
            .WithMany()
            .HasForeignKey(l => l.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Ignore(l => l.IsActive);
    }
}
