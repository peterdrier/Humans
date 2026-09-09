using Humans.Surveys.Contracts;
using System.Text.Json;
using Humans.Surveys.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Surveys.Data.Configurations;

internal sealed class SurveyAnswerConfiguration : IEntityTypeConfiguration<SurveyAnswer>
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

        b.Property(a => a.GridSelections).HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, SurveyJson.Options),
                v => JsonSerializer.Deserialize<Dictionary<string, List<string>>>(v, SurveyJson.Options),
                new ValueComparer<Dictionary<string, List<string>>?>(
                    (a, c) => JsonSerializer.Serialize(a, SurveyJson.Options) == JsonSerializer.Serialize(c, SurveyJson.Options),
                    v => v == null ? 0 : string.GetHashCode(JsonSerializer.Serialize(v, SurveyJson.Options), StringComparison.Ordinal),
                    v => v == null ? null : JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
                        JsonSerializer.Serialize(v, SurveyJson.Options), SurveyJson.Options)));

        b.Property(a => a.RankedValue).HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, SurveyJson.Options),
                v => JsonSerializer.Deserialize<RankedAnswer>(v, SurveyJson.Options),
                new ValueComparer<RankedAnswer?>(
                    (a, c) => JsonSerializer.Serialize(a, SurveyJson.Options)
                        == JsonSerializer.Serialize(c, SurveyJson.Options),
                    v => v == null
                        ? 0
                        : string.GetHashCode(
                            JsonSerializer.Serialize(v, SurveyJson.Options),
                            StringComparison.Ordinal),
                    v => v == null
                        ? null
                        : JsonSerializer.Deserialize<RankedAnswer>(
                            JsonSerializer.Serialize(v, SurveyJson.Options),
                            SurveyJson.Options)));

        b.Property(a => a.TextValue).HasMaxLength(4000);

        // Intra-section FK to the question; Restrict so a question can't be deleted out from under answers.
        b.HasOne<SurveyQuestion>().WithMany()
            .HasForeignKey(a => a.QuestionId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(a => a.ResponseId);
        b.HasIndex(a => a.QuestionId);
    }
}
