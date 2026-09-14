using System.Linq.Expressions;
using System.Text.Json;
using Humans.Governance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Governance.Data.Configurations;

/// <summary>
/// Shared jsonb plumbing for Governance's assembly-vote tables: one cached
/// <see cref="JsonSerializerOptions"/> (CA1869) plus the two property converters the
/// entities need. Mirrors Surveys' <c>SurveyJson</c>, which is the precedent for
/// culture-keyed jsonb in this codebase.
/// </summary>
internal static class GovernanceJson
{
    public static readonly JsonSerializerOptions Options = new();

    /// <summary>Maps a <see cref="GovernanceLocalizedText"/> property to a jsonb culture→text column.</summary>
    public static void LocalizedText<TEntity>(
        EntityTypeBuilder<TEntity> b,
        Expression<Func<TEntity, GovernanceLocalizedText>> prop) where TEntity : class
        => b.Property(prop)
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v.Values, Options),
                v => new GovernanceLocalizedText(
                    JsonSerializer.Deserialize<Dictionary<string, string>>(v, Options)
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
                new ValueComparer<GovernanceLocalizedText>(
                    (a, c) => a == null ? c == null : a.Equals(c),
                    v => v.GetHashCode(),
                    v => v));

    /// <summary>
    /// Maps a ballot's preference order to a jsonb string array. Order is the meaning of
    /// the value, so the comparer is sequence-sensitive — a reordered ranking is a changed
    /// ballot, and EF must see it as one.
    /// </summary>
    public static void Ranking<TEntity>(
        EntityTypeBuilder<TEntity> b,
        Expression<Func<TEntity, IReadOnlyList<string>?>> prop) where TEntity : class
        => b.Property(prop)
            .HasColumnType("jsonb")
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, Options),
                v => v == null
                    ? null
                    : JsonSerializer.Deserialize<List<string>>(v, Options),
                new ValueComparer<IReadOnlyList<string>?>(
                    (a, c) => a == null ? c == null : c != null && a.SequenceEqual(c, StringComparer.Ordinal),
                    v => v == null
                        ? 0
                        : v.Aggregate(0, (h, s) => HashCode.Combine(h, StringComparer.Ordinal.GetHashCode(s))),
                    v => v == null ? null : v.ToList()));
}
