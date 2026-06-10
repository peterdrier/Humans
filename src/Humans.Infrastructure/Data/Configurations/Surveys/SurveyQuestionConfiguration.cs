using System.Text.Json;
using Humans.Domain.Entities;
using Humans.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Surveys;

public class SurveyQuestionConfiguration : IEntityTypeConfiguration<SurveyQuestion>
{
    public void Configure(EntityTypeBuilder<SurveyQuestion> b)
    {
        b.ToTable("survey_questions");
        b.HasKey(q => q.Id);

        SurveyJson.LocalizedText(b, q => q.Prompt);
        SurveyJson.LocalizedText(b, q => q.HelpText);
        SurveyJson.LocalizedText(b, q => q.RatingMinLabel);
        SurveyJson.LocalizedText(b, q => q.RatingMaxLabel);

        b.Property(q => q.Type).HasConversion<string>().HasMaxLength(20);

        b.Property(q => q.ShowIf)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, SurveyJson.Options),
                v => JsonSerializer.Deserialize<BranchCondition>(v, SurveyJson.Options),
                new ValueComparer<BranchCondition?>(
                    (a, c) => a == null
                        ? c == null
                        : c != null && JsonSerializer.Serialize(a, SurveyJson.Options) == JsonSerializer.Serialize(c, SurveyJson.Options),
                    v => v == null ? 0 : string.GetHashCode(JsonSerializer.Serialize(v, SurveyJson.Options), StringComparison.Ordinal),
                    v => v == null ? null : JsonSerializer.Deserialize<BranchCondition>(JsonSerializer.Serialize(v, SurveyJson.Options), SurveyJson.Options)));

        b.HasMany(q => q.Options).WithOne(o => o.Question)
            .HasForeignKey(o => o.QuestionId).OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(q => new { q.SurveyId, q.PageNumber, q.Order });
    }
}
