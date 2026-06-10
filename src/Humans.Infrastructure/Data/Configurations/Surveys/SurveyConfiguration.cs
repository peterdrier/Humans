using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Surveys;

public class SurveyConfiguration : IEntityTypeConfiguration<Survey>
{
    public void Configure(EntityTypeBuilder<Survey> b)
    {
        b.ToTable("surveys");
        b.HasKey(s => s.Id);

        SurveyJson.LocalizedText(b, s => s.Title);
        SurveyJson.LocalizedText(b, s => s.Intro);
        SurveyJson.LocalizedText(b, s => s.ThankYou);

        b.Property(s => s.DefaultCulture).HasMaxLength(10).IsRequired();
        b.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(s => s.AudienceType).HasConversion<string>().HasMaxLength(20);
        b.Property(s => s.PublicSlug).HasMaxLength(80);

        b.HasIndex(s => s.Status);
        b.HasIndex(s => s.PublicSlug).IsUnique().HasFilter("\"PublicSlug\" IS NOT NULL");

        b.HasMany(s => s.Questions).WithOne(q => q.Survey)
            .HasForeignKey(q => q.SurveyId).OnDelete(DeleteBehavior.Cascade);

        // CreatedByUserId / AudienceTeamId are bare Guid columns: NO navigation property and NO
        // cross-section EF FK constraint (FeedbackReport.AgentConversationId precedent). The service
        // resolves the creator's display name / team via IUserServiceRead / ITeamServiceRead when needed.
    }
}
