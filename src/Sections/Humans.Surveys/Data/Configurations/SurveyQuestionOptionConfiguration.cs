using Humans.Surveys.Contracts;
using Humans.Surveys.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Surveys.Data.Configurations;

internal sealed class SurveyQuestionOptionConfiguration : IEntityTypeConfiguration<SurveyQuestionOption>
{
    public void Configure(EntityTypeBuilder<SurveyQuestionOption> b)
    {
        b.ToTable("survey_question_options");
        b.HasKey(o => o.Id);

        b.Property(o => o.Value).HasMaxLength(100).IsRequired();
        SurveyJson.LocalizedText(b, o => o.Label);

        b.HasIndex(o => new { o.QuestionId, o.Order });
    }
}
