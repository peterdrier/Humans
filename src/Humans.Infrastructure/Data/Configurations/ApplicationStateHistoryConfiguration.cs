using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations;

public class ApplicationStateHistoryConfiguration : IEntityTypeConfiguration<ApplicationStateHistory>
{
    public void Configure(EntityTypeBuilder<ApplicationStateHistory> builder)
    {
        builder.ToTable("application_state_history");

        builder.HasKey(sh => sh.Id);

        builder.Property(sh => sh.Status)
            .IsRequired()
            .HasConversion<string>();

        builder.Property(sh => sh.ChangedAt)
            .IsRequired();

        builder.Property(sh => sh.Notes)
            .HasMaxLength(4000);

        // FK-only relationship to User — the cross-domain nav property was
        // stripped in the Governance migration (design-rules §6).
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(sh => sh.ChangedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(sh => sh.ApplicationId);
        builder.HasIndex(sh => sh.ChangedAt);
    }
}
