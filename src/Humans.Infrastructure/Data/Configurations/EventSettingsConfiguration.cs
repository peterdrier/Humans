using System.Text.Json;
using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class EventSettingsConfiguration : IEntityTypeConfiguration<EventSettings>
{
    public void Configure(EntityTypeBuilder<EventSettings> builder)
    {
        builder.ToTable("event_settings");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.EventName).HasMaxLength(256).IsRequired();
        builder.Property(e => e.TimeZoneId).HasMaxLength(100).IsRequired();

        var dictComparer = new ValueComparer<Dictionary<int, int>>(
            (a, b) => DictionaryEquals(a, b),
            v => DictionaryHash(v),
            v => new Dictionary<int, int>(v));

        builder.Property(e => e.EarlyEntryCapacity).HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<Dictionary<int, int>>(v, (JsonSerializerOptions?)null) ?? new(),
                dictComparer);

        var nullableDictComparer = new ValueComparer<Dictionary<int, int>?>(
            (a, b) => NullableDictionaryEquals(a, b),
            v => v == null ? 0 : DictionaryHash(v),
            v => v == null ? null : new Dictionary<int, int>(v));

        builder.Property(e => e.BarriosEarlyEntryAllocation).HasColumnType("jsonb")
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => v == null ? null : JsonSerializer.Deserialize<Dictionary<int, int>>(v, (JsonSerializerOptions?)null),
                nullableDictComparer);

        builder.HasIndex(e => e.IsActive);
    }

    private static bool DictionaryEquals(Dictionary<int, int>? a, Dictionary<int, int>? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var val) || val != kv.Value)
                return false;
        }
        return true;
    }

    private static bool NullableDictionaryEquals(Dictionary<int, int>? a, Dictionary<int, int>? b) =>
        DictionaryEquals(a, b);

    private static int DictionaryHash(Dictionary<int, int> v) =>
        v.Aggregate(0, (hash, kv) => HashCode.Combine(hash, kv.Key, kv.Value));
}
