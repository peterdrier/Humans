using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Shifts;

public class ShiftTagConfiguration : IEntityTypeConfiguration<ShiftTag>
{
    public void Configure(EntityTypeBuilder<ShiftTag> builder)
    {
        builder.ToTable("shift_tags");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Name)
            .HasMaxLength(100)
            .IsRequired();

        builder.HasIndex(t => t.Name)
            .IsUnique()
            .HasDatabaseName("IX_shift_tags_name_unique");

        builder.HasMany(t => t.Rotas)
            .WithMany(r => r.Tags)
            .UsingEntity<Dictionary<string, object>>(
                "RotaShiftTag",
                j => j.HasOne<Rota>().WithMany().HasForeignKey("RotaId").OnDelete(DeleteBehavior.Cascade),
                j => j.HasOne<ShiftTag>().WithMany().HasForeignKey("ShiftTagId").OnDelete(DeleteBehavior.Cascade),
                j =>
                {
                    j.ToTable("rota_shift_tags");
                    j.HasKey("RotaId", "ShiftTagId");
                });

        // Reserved GUID block: 0003. See docs/guid-reservations.md.
        // Seed initial tags from coordinator feedback.
        builder.HasData(
            new { Id = new Guid("00000000-0000-0000-0003-000000000001"), Name = "Heavy lifting" },
            new { Id = new Guid("00000000-0000-0000-0003-000000000002"), Name = "Working in the sun" },
            new { Id = new Guid("00000000-0000-0000-0003-000000000003"), Name = "Working in the shade" },
            new { Id = new Guid("00000000-0000-0000-0003-000000000004"), Name = "Organisational task" },
            new { Id = new Guid("00000000-0000-0000-0003-000000000005"), Name = "Meeting new people" },
            new { Id = new Guid("00000000-0000-0000-0003-000000000006"), Name = "Looking after folks" },
            new { Id = new Guid("00000000-0000-0000-0003-000000000007"), Name = "Exploring the site" },
            new { Id = new Guid("00000000-0000-0000-0003-000000000008"), Name = "Feeding and hydrating folks" });
    }
}
