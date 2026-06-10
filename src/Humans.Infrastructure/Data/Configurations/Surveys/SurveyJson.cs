using System.Linq.Expressions;
using System.Text.Json;
using Humans.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Surveys;

/// <summary>
/// Shared jsonb plumbing for the Survey section. One cached <see cref="JsonSerializerOptions"/>
/// (CA1869) and a reusable <see cref="LocalizedText"/> property converter mirroring the
/// DocumentVersion jsonb precedent.
/// </summary>
internal static class SurveyJson
{
    public static readonly JsonSerializerOptions Options = new();

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
}
