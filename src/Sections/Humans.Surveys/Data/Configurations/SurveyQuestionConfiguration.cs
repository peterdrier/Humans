using Humans.Surveys.Contracts;
using System.Text.Json;
using Humans.Surveys.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Surveys.Data.Configurations;

internal sealed class SurveyQuestionConfiguration : IEntityTypeConfiguration<SurveyQuestion>
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
        b.Property(q => q.GridSelectionMode).HasConversion<string>().HasMaxLength(20);

        b.Property(q => q.GridRows)
            .HasColumnType("jsonb")
            .HasConversion(
                v => SurveyJson.SerializeGridRows(v),
                v => SurveyJson.DeserializeGridRows(v),
                new ValueComparer<List<SurveyGridRow>?>(
                    (a, c) => SurveyJson.SerializeGridRows(a) == SurveyJson.SerializeGridRows(c),
                    v => v == null ? 0 : string.GetHashCode(SurveyJson.SerializeGridRows(v)!, StringComparison.Ordinal),
                    v => SurveyJson.DeserializeGridRows(SurveyJson.SerializeGridRows(v))));

        b.Property(q => q.InformationImages)
            .HasColumnType("jsonb")
            .HasConversion(
                v => SurveyJson.SerializeInformationImages(v),
                v => SurveyJson.DeserializeInformationImages(v),
                new ValueComparer<List<SurveyInformationImage>?>(
                    (a, c) => SurveyJson.SerializeInformationImages(a) == SurveyJson.SerializeInformationImages(c),
                    v => v == null ? 0 : string.GetHashCode(SurveyJson.SerializeInformationImages(v)!, StringComparison.Ordinal),
                    v => SurveyJson.DeserializeInformationImages(SurveyJson.SerializeInformationImages(v))));

        b.Property(q => q.RankedSettings)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, SurveyJson.Options),
                v => JsonSerializer.Deserialize<RankedQuestionSettings>(v, SurveyJson.Options),
                new ValueComparer<RankedQuestionSettings?>(
                    (a, c) => JsonSerializer.Serialize(a, SurveyJson.Options)
                        == JsonSerializer.Serialize(c, SurveyJson.Options),
                    v => v == null
                        ? 0
                        : string.GetHashCode(
                            JsonSerializer.Serialize(v, SurveyJson.Options),
                            StringComparison.Ordinal),
                    v => v == null
                        ? null
                        : JsonSerializer.Deserialize<RankedQuestionSettings>(
                            JsonSerializer.Serialize(v, SurveyJson.Options),
                            SurveyJson.Options)));

        b.Property(q => q.RankedUnavailableOptionValues)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, SurveyJson.Options),
                v => JsonSerializer.Deserialize<List<string>>(v, SurveyJson.Options),
                new ValueComparer<List<string>?>(
                    (a, c) => JsonSerializer.Serialize(a, SurveyJson.Options)
                        == JsonSerializer.Serialize(c, SurveyJson.Options),
                    v => v == null
                        ? 0
                        : string.GetHashCode(
                            JsonSerializer.Serialize(v, SurveyJson.Options),
                            StringComparison.Ordinal),
                    v => v == null
                        ? null
                        : JsonSerializer.Deserialize<List<string>>(
                            JsonSerializer.Serialize(v, SurveyJson.Options),
                            SurveyJson.Options)));

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
