using Humans.Holded.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Holded.Data.Configurations;

internal sealed class HoldedApiCallConfiguration : IEntityTypeConfiguration<HoldedApiCall>
{
    public void Configure(EntityTypeBuilder<HoldedApiCall> b)
    {
        b.ToTable("holded_api_calls");
        b.HasKey(x => x.Id);
        b.Property(x => x.Endpoint).HasMaxLength(64);
        b.Property(x => x.Method).HasMaxLength(8);
        b.Property(x => x.RateLimitWindow).HasMaxLength(16);
        // The admin screen groups by month, so the read is always date-ranged.
        b.HasIndex(x => x.CalledAt);
    }
}
