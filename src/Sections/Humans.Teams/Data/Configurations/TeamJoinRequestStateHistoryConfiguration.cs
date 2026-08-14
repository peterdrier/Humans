using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Teams.Domain;

namespace Humans.Teams.Data.Configurations;

internal sealed class TeamJoinRequestStateHistoryConfiguration : IEntityTypeConfiguration<TeamJoinRequestStateHistory>
{
    public void Configure(EntityTypeBuilder<TeamJoinRequestStateHistory> builder)
    {
        builder.ToTable("team_join_request_state_history");

        builder.HasKey(sh => sh.Id);

        builder.Property(sh => sh.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(sh => sh.ChangedAt)
            .IsRequired();

        builder.Property(sh => sh.Notes)
            .HasMaxLength(2000);

        // ChangedByUserId is a bare cross-section Guid column — no FK constraint, no nav.

        builder.HasIndex(sh => sh.TeamJoinRequestId);
        builder.HasIndex(sh => sh.ChangedAt);
    }
}
