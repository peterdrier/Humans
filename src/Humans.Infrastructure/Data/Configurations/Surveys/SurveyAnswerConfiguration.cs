using System.Text.Json;
using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Surveys;

public class SurveyAnswerConfiguration : IEntityTypeConfiguration<SurveyAnswer>
{
    public void Configure(EntityTypeBuilder<SurveyAnswer> b)
    {
        b.ToTable("survey_answers");
        b.HasKey(a => a.Id);

        // Selected option values as jsonb (List<string>) — mirrors ProfileConfiguration.Allergies.
        b.Property(a => a.SelectedOptionValues).HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, SurveyJson.Options),
                v => JsonSerializer.Deserialize<List<string>>(v, SurveyJson.Options) ?? new(),
                new ValueComparer<List<string>>(
                    (a, c) => a != null && c != null && a.SequenceEqual(c),
                    v => v.Aggregate(0, HashCode.Combine),
                    v => v.ToList()));

        b.Property(a => a.TextValue).HasMaxLength(4000);

        // Intra-section FK to the question; Restrict so a question can't be deleted out from under answers.
        b.HasOne<SurveyQuestion>().WithMany()
            .HasForeignKey(a => a.QuestionId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(a => a.ResponseId);
        b.HasIndex(a => a.QuestionId);
    }
}
