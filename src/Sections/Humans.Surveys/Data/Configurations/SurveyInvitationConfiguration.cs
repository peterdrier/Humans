using Humans.Surveys.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Surveys.Data.Configurations;

internal sealed class SurveyInvitationConfiguration : IEntityTypeConfiguration<SurveyInvitation>
{
    public void Configure(EntityTypeBuilder<SurveyInvitation> b)
    {
        b.ToTable("survey_invitations");
        b.HasKey(i => i.Id);

        b.Property(i => i.LatestEmailStatus).HasConversion<string>().HasMaxLength(20);

        // The idempotent send diffs against this.
        b.HasIndex(i => new { i.SurveyId, i.UserId }).IsUnique();
        b.HasIndex(i => new { i.SurveyId, i.Completed, i.SentAt });

        // Completed is a plain bool — NO completion-time column and NO UpdatedAt (timing side-channel).
    }
}
