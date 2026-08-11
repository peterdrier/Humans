using Humans.Governance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Governance.Data.Configurations;

internal sealed class ApplicationStateHistoryConfiguration : IEntityTypeConfiguration<ApplicationStateHistory>
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

        // ChangedByUserId is a bare cross-section Guid column — no FK constraint, no nav.

        builder.HasIndex(sh => sh.ApplicationId);
        builder.HasIndex(sh => sh.ChangedAt);
    }
}
