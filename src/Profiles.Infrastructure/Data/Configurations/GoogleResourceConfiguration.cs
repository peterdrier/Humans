using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Profiles.Domain.Entities;

namespace Profiles.Infrastructure.Data.Configurations;

public class GoogleResourceConfiguration : IEntityTypeConfiguration<GoogleResource>
{
    public void Configure(EntityTypeBuilder<GoogleResource> builder)
    {
        builder.ToTable("google_resources");

        builder.HasKey(gr => gr.Id);

        builder.Property(gr => gr.ResourceType)
            .IsRequired()
            .HasConversion<string>();

        builder.Property(gr => gr.GoogleId)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(gr => gr.Name)
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(gr => gr.Url)
            .HasMaxLength(2048);

        builder.Property(gr => gr.ProvisionedAt)
            .IsRequired();

        builder.Property(gr => gr.ErrorMessage)
            .HasMaxLength(2000);

        builder.HasOne(gr => gr.User)
            .WithMany()
            .HasForeignKey(gr => gr.UserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(gr => gr.GoogleId);

        builder.HasIndex(gr => gr.TeamId);
        builder.HasIndex(gr => gr.UserId);
        builder.HasIndex(gr => gr.IsActive);

        // Filtered unique index: one active resource per (Team, GoogleId)
        builder.HasIndex(gr => new { gr.TeamId, gr.GoogleId })
            .HasFilter("\"IsActive\" = true AND \"TeamId\" IS NOT NULL")
            .IsUnique();
    }
}
