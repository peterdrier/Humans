using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Profiles.Domain.Entities;

namespace Profiles.Infrastructure.Data.Configurations;

public class ContactFieldConfiguration : IEntityTypeConfiguration<ContactField>
{
    public void Configure(EntityTypeBuilder<ContactField> builder)
    {
        builder.ToTable("contact_fields");

        builder.HasKey(cf => cf.Id);

        builder.Property(cf => cf.FieldType)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(cf => cf.CustomLabel)
            .HasMaxLength(100);

        builder.Property(cf => cf.Value)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(cf => cf.Visibility)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(cf => cf.DisplayOrder)
            .IsRequired();

        builder.Property(cf => cf.CreatedAt)
            .IsRequired();

        builder.Property(cf => cf.UpdatedAt)
            .IsRequired();

        builder.HasOne(cf => cf.Profile)
            .WithMany(p => p.ContactFields)
            .HasForeignKey(cf => cf.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(cf => cf.ProfileId);
        builder.HasIndex(cf => new { cf.ProfileId, cf.Visibility });

        // Ignore computed property
        builder.Ignore(cf => cf.DisplayLabel);
    }
}
