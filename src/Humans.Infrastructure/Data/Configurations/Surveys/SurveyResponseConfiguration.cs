using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Surveys;

public class SurveyResponseConfiguration : IEntityTypeConfiguration<SurveyResponse>
{
    public void Configure(EntityTypeBuilder<SurveyResponse> b)
    {
        b.ToTable("survey_responses");
        b.HasKey(r => r.Id);

        b.Property(r => r.Anonymity).HasConversion<string>().HasMaxLength(20);
        b.Property(r => r.InputMethod).HasConversion<string>().HasMaxLength(20);
        b.Property(r => r.Culture).HasMaxLength(10);

        b.HasMany(r => r.Answers).WithOne(a => a.Response)
            .HasForeignKey(a => a.ResponseId).OnDelete(DeleteBehavior.Cascade);

        // Intra-section FK to the invitation (set only for Identified); SetNull keeps responses if an
        // invitation row is ever removed. UserId is a bare Guid? column — no nav, no cross-section FK.
        b.HasOne<SurveyInvitation>().WithMany()
            .HasForeignKey(r => r.InvitationId).OnDelete(DeleteBehavior.SetNull);

        b.HasIndex(r => r.SurveyId);
        b.HasIndex(r => new { r.SurveyId, r.UserId });
    }
}
