using Humans.Calendar.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Calendar.Data.Configurations;

internal sealed class CalendarFeedTokenConfiguration : IEntityTypeConfiguration<CalendarFeedToken>
{
    public void Configure(EntityTypeBuilder<CalendarFeedToken> builder)
    {
        builder.ToTable("calendar_feed_tokens");

        // ValueGeneratedNever: otherwise EF would invent an id for a Guid.Empty write instead of failing loudly.
        builder.HasKey(x => x.UserId);
        builder.Property(x => x.UserId).ValueGeneratedNever();

        builder.Property(x => x.Token).IsRequired();

        // No index on Token: validation loads by UserId (in the URL) and compares.
    }
}
