using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations;

public class ConsentHoldListEntryConfiguration : IEntityTypeConfiguration<ConsentHoldListEntry>
{
    public void Configure(EntityTypeBuilder<ConsentHoldListEntry> builder)
    {
        builder.ToTable("consent_hold_list");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Entry)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(e => e.Note)
            .HasMaxLength(2000);

        builder.Property(e => e.AddedByUserId)
            .IsRequired();

        builder.Property(e => e.AddedAt)
            .IsRequired();

        // FK-only to User — no nav property on the User side.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(e => e.AddedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
