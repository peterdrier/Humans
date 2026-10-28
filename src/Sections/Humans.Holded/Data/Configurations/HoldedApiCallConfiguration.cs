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
        // For date-ranged reads. Today the admin screen loads every call and groups by month in memory.
        b.HasIndex(x => x.CalledAt);
    }
}
