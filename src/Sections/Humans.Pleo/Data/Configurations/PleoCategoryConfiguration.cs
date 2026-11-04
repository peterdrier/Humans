using Humans.Pleo.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Pleo.Data.Configurations;

internal sealed class PleoCategoryConfiguration : IEntityTypeConfiguration<PleoCategory>
{
    public void Configure(EntityTypeBuilder<PleoCategory> b)
    {
        b.ToTable("pleo_categories");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        // No unique index on Name (memory/architecture/unique-constraints-ids-only.md): the service refuses a duplicate.
        b.Property(x => x.Name).HasMaxLength(128);
        b.Property(x => x.HoldedCode).HasMaxLength(16);
        b.Property(x => x.PleoCategoryId).HasMaxLength(64);
        b.HasIndex(x => x.PleoCategoryId).IsUnique();

        // Pleo → Settings → Accounting → Categories, group "Holded Accounts", 3 Nov 2026. Ids are linked by hand.
        b.HasData(
            Seed("f2fe14a1-c8b7-43ea-b6eb-dfb73da28541", "Dept - La Cantina", "62900114"),
            Seed("ec1e51be-d96b-453f-8318-c79371282045", "Dept - Power", "62900117"),
            Seed("b3094c58-d32c-4f32-ab25-bcfd9737d1b7", "Dept - Production", "62900118"),
            Seed("963cecd9-2299-438c-aeb0-15dacba3a10d", "Dept - Creativity", "62900109"),
            Seed("7f07ac5e-f72e-417c-b299-7e35354736e5", "Dept - Infrastructure", "62900112"),
            Seed("11d69694-1795-4a99-bd86-b60717f61c87", "Dept - LNT", "62900113"),
            Seed("4ecb4d76-813c-4251-99ed-48d14202f3d2", "Dept - City Planning", "62900107"),
            Seed("1703039b-0395-4606-96eb-1d108bf25393", "Dept - Barrio Support", "62900139"),
            Seed("06380c3d-eea8-4db0-8177-af4a2fd6fe57", "Dept - NoInfo", "62900115"),
            Seed("0e08a552-946a-4839-a48b-6144fd58d57c", "Dept - Participant Wellness", "62900116"),
            Seed("6a9ddd36-f240-41fc-9af1-ca03a5d37b46", "Dept - GGP", "62900110"),
            Seed("52754e08-1f8f-4fd7-aa66-9e8e8c55d617", "Dept - Inclusion", "62900111"),
            Seed("cde2932c-849a-44c4-8118-2eec7997f862", "WG - ALM 2026", "62900141"));
    }

    private static PleoCategory Seed(string id, string name, string holdedCode) =>
        new() { Id = Guid.Parse(id), Name = name, HoldedCode = holdedCode };
}
