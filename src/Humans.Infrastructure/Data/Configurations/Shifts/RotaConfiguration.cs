using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Shifts;

public class RotaConfiguration : IEntityTypeConfiguration<Rota>
{
    public void Configure(EntityTypeBuilder<Rota> builder)
    {
        builder.ToTable("rotas");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Name).HasMaxLength(256).IsRequired();
        builder.Property(r => r.Description).HasMaxLength(2000);
        builder.Property(r => r.Priority).HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(r => r.Policy).HasConversion<string>().HasMaxLength(50).IsRequired();

        builder.Property(e => e.Period)
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(e => e.PracticalInfo)
            .HasMaxLength(2000);

        builder.Property(r => r.IsVisibleToVolunteers)
            .HasDefaultValue(true)
            .HasSentinel(true);

        builder.HasIndex(r => new { r.EventSettingsId, r.TeamId });

        builder.HasOne(r => r.EventSettings)
            .WithMany(e => e.Rotas)
            .HasForeignKey(r => r.EventSettingsId)
            .OnDelete(DeleteBehavior.Restrict);

        // EF needs the nav ref to configure the cross-section FK + cascade.
        // The nav is [Obsolete] for the Application layer (design-rules §6c)
        // — suppress only for this wiring block.
#pragma warning disable CS0618
        builder.HasOne(r => r.Team)
            .WithMany()
            .HasForeignKey(r => r.TeamId)
            .OnDelete(DeleteBehavior.Restrict);
#pragma warning restore CS0618
    }
}
