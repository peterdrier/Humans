using System.Linq.Expressions;
using System.Text.Json;
using Humans.Surveys.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Surveys.Data.Configurations;

/// <summary>
/// Shared jsonb plumbing for the Survey section. One cached <see cref="JsonSerializerOptions"/>
/// (CA1869) and a reusable <see cref="LocalizedText"/> property converter mirroring the
/// DocumentVersion jsonb precedent.
/// </summary>
internal static class SurveyJson
{
    public static readonly JsonSerializerOptions Options = new();

    private sealed record GridRowDocument(string Value, Dictionary<string, string> Label);
    private sealed record InformationImageDocument(
        Guid Id,
        string StoragePath,
        string ContentType,
        string FileName,
        Dictionary<string, string> Label,
        Dictionary<string, string> AltText);

    /// <summary>Maps a <see cref="LocalizedText"/> property to a jsonb column (culture→text dictionary).</summary>
    public static void LocalizedText<TEntity>(
        EntityTypeBuilder<TEntity> b,
        Expression<Func<TEntity, LocalizedText>> prop) where TEntity : class
        => b.Property(prop)
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v.Values, Options),
                v => new LocalizedText(JsonSerializer.Deserialize<Dictionary<string, string>>(v, Options)
                                       ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
                new ValueComparer<LocalizedText>(
                    (a, c) => a == null ? c == null : a.Equals(c),
                    v => v.GetHashCode(),
                    v => v));

    /// <summary>
    /// Serializes Grid rows through a persistence-only shape because <see cref="LocalizedText"/>
    /// deliberately exposes a read-only dictionary and is not directly deserializable by
    /// <see cref="JsonSerializer"/>.
    /// </summary>
    public static string? SerializeGridRows(List<SurveyGridRow>? rows)
        => rows is null
            ? null
            : JsonSerializer.Serialize(
                rows.Select(row => new GridRowDocument(
                    row.Value,
                    new Dictionary<string, string>(row.Label.Values, StringComparer.OrdinalIgnoreCase))),
                Options);

    public static List<SurveyGridRow>? DeserializeGridRows(string? value)
        => value is null
            ? null
            : JsonSerializer.Deserialize<List<GridRowDocument>>(value, Options)?
                .Select(row => new SurveyGridRow(
                    row.Value,
                    new LocalizedText(row.Label)))
                .ToList();

    public static string? SerializeInformationImages(List<SurveyInformationImage>? images)
        => images is null
            ? null
            : JsonSerializer.Serialize(
                images.Select(image => new InformationImageDocument(
                    image.Id,
                    image.StoragePath,
                    image.ContentType,
                    image.FileName,
                    new Dictionary<string, string>(image.Label.Values, StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, string>(image.AltText.Values, StringComparer.OrdinalIgnoreCase))),
                Options);

    public static List<SurveyInformationImage>? DeserializeInformationImages(string? value)
        => value is null
            ? null
            : JsonSerializer.Deserialize<List<InformationImageDocument>>(value, Options)?
                .Select(image => new SurveyInformationImage(
                    image.Id,
                    image.StoragePath,
                    image.ContentType,
                    image.FileName,
                    new LocalizedText(image.Label),
                    new LocalizedText(image.AltText)))
                .ToList();
}
