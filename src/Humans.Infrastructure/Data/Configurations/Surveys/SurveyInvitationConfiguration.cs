using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Surveys;

public class SurveyInvitationConfiguration : IEntityTypeConfiguration<SurveyInvitation>
{
    public void Configure(EntityTypeBuilder<SurveyInvitation> b)
    {
        b.ToTable("survey_invitations");
        b.HasKey(i => i.Id);

        b.Property(i => i.LatestEmailStatus).HasConversion<string>().HasMaxLength(20);

        // One invitation per (survey, user) — the idempotent send model diffs against this.
        b.HasIndex(i => new { i.SurveyId, i.UserId }).IsUnique();
        // Reminder sweep predicate index.
        b.HasIndex(i => new { i.SurveyId, i.Completed, i.SentAt });

        // Completed is a plain bool — NO completion-time column and NO UpdatedAt (timing side-channel, plan
        // Deviation #10). UserId is a bare Guid column — no nav, no cross-section EF FK constraint.
    }
}
