using System.Text.Json;
using Humans.Domain.Entities;
using Humans.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class CampConfiguration : IEntityTypeConfiguration<Camp>
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    public void Configure(EntityTypeBuilder<Camp> builder)
    {
        builder.ToTable("camps");

        builder.Property(b => b.Slug).HasMaxLength(256).IsRequired();
        builder.Property(b => b.ContactEmail).HasMaxLength(256).IsRequired();
        builder.Property(b => b.ContactPhone).HasMaxLength(64).IsRequired();
        builder.Property(b => b.WebOrSocialUrl).HasMaxLength(512);

        builder.Property(b => b.Links).HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonOptions),
                v => JsonSerializer.Deserialize<List<CampLink>>(v, JsonOptions) ?? new(),
                new ValueComparer<List<CampLink>>(
                    (a, b) => a == null ? b == null : b != null && JsonSerializer.Serialize(a, JsonOptions) == JsonSerializer.Serialize(b, JsonOptions),
                    v => string.GetHashCode(JsonSerializer.Serialize(v, JsonOptions), StringComparison.Ordinal),
                    v => JsonSerializer.Deserialize<List<CampLink>>(JsonSerializer.Serialize(v, JsonOptions), JsonOptions)!));

        builder.HasIndex(b => b.Slug).IsUnique();

        builder.HasOne(b => b.CreatedByUser)
            .WithMany()
            .HasForeignKey(b => b.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(b => b.Seasons)
            .WithOne(s => s.Camp)
            .HasForeignKey(s => s.CampId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(b => b.Leads)
            .WithOne(l => l.Camp)
            .HasForeignKey(l => l.CampId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(b => b.HistoricalNames)
            .WithOne(h => h.Camp)
            .HasForeignKey(h => h.CampId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(b => b.Images)
            .WithOne(i => i.Camp)
            .HasForeignKey(i => i.CampId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
