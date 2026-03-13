using System.Text.Json;
using System.Text.Json.Serialization;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class BarrioSeasonConfiguration : IEntityTypeConfiguration<BarrioSeason>
{
    private static readonly JsonSerializerOptions JsonEnumOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public void Configure(EntityTypeBuilder<BarrioSeason> builder)
    {
        builder.ToTable("barrio_seasons");

        builder.Property(s => s.Name).HasMaxLength(256).IsRequired();
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(s => s.BlurbLong).HasMaxLength(4000).IsRequired();
        builder.Property(s => s.BlurbShort).HasMaxLength(1000).IsRequired();
        builder.Property(s => s.Languages).HasMaxLength(256).IsRequired();

        builder.Property(s => s.AcceptingMembers).HasConversion<string>().HasMaxLength(50);
        builder.Property(s => s.KidsWelcome).HasConversion<string>().HasMaxLength(50);
        builder.Property(s => s.KidsVisiting).HasConversion<string>().HasMaxLength(50);
        builder.Property(s => s.KidsAreaDescription).HasMaxLength(2000);

        builder.Property(s => s.HasPerformanceSpace).HasConversion<string>().HasMaxLength(50);
        builder.Property(s => s.PerformanceTypes).HasMaxLength(1000);

        builder.Property(s => s.Vibes).HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonEnumOptions),
                v => JsonSerializer.Deserialize<List<BarrioVibe>>(v, JsonEnumOptions) ?? new());

        builder.Property(s => s.AdultPlayspace).HasConversion<string>().HasMaxLength(50);

        builder.Property(s => s.SpaceRequirement).HasConversion<string>().HasMaxLength(50);
        builder.Property(s => s.SoundZone).HasConversion<string>().HasMaxLength(50);
        builder.Property(s => s.ContainerNotes).HasMaxLength(2000);
        builder.Property(s => s.ElectricalGrid).HasConversion<string>().HasMaxLength(50);

        builder.Property(s => s.ReviewNotes).HasMaxLength(2000);

        builder.HasIndex(s => new { s.BarrioId, s.Year }).IsUnique();
        builder.HasIndex(s => s.Status);

        builder.HasOne(s => s.ReviewedByUser)
            .WithMany()
            .HasForeignKey(s => s.ReviewedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
