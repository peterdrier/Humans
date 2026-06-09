using System.Globalization;
using System.Reflection;
using Humans.Application.Extensions;
using NodaTime;
using NodaTime.Text;

namespace Humans.Web.Models;

/// <summary>
/// Reflection-built catalog of every formatter on <see cref="DateFormattingExtensions"/>
/// (the single HUM0030 home), rendered against one fixed sample so a developer can see
/// what exists, what each one produces, and spot formatters that earn no keep. Powers
/// <c>/Debug/FormatGallery</c>. Reflection — not a hand list — so a new formatter can
/// never hide from the audit.
/// </summary>
public sealed record FormatGalleryViewModel(
    string SampleUtc,
    string SampleLocal,
    string SampleZone,
    IReadOnlyList<FormatterCard> CultureSensitive,
    IReadOnlyList<FormatterCard> CultureStable,
    IReadOnlyList<PatternCard> Patterns);

/// <summary>One string-returning formatter method (overloads collapsed to a single card).</summary>
public sealed record FormatterCard(
    string Name,
    string InputType,
    string EsOutput,
    string EnOutput,
    IReadOnlyList<string> SameOutputAs);

/// <summary>One NodaTime <c>*Pattern</c> field — its pattern text and a sample render.</summary>
public sealed record PatternCard(
    string Name,
    string ValueType,
    string PatternText,
    string SampleOutput);

public static class FormatGalleryModelBuilder
{
    // A fixed UTC instant, then converted: afternoon (16:23 local, so 24h time reads
    // clearly), day 25 (> 12, so it can't be mistaken for a month), and August (es "ago"
    // / "agosto" vs en "Aug" / "August" — distinct in both abbrev and full, unlike
    // Jun/Jul whose abbreviations match across the two cultures).
    private static readonly Instant SampleInstant = Instant.FromUtc(2026, 8, 25, 14, 23, 7);
    private static readonly DateTimeZone SampleZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    // When several overloads share a name, pick the friendliest input to render.
    private static readonly Type[] InputPreference =
    [
        typeof(DateTime), typeof(DateTimeOffset), typeof(LocalDateTime),
        typeof(LocalDate), typeof(LocalTime), typeof(Instant),
    ];

    public static FormatGalleryViewModel Build()
    {
        var zoned = SampleInstant.InZone(SampleZone);
        var local = zoned.LocalDateTime; // 2026-08-25T16:23:07

        // One sample value per first-parameter type the formatters accept.
        var samples = new Dictionary<Type, object>
        {
            [typeof(DateTime)] = local.ToDateTimeUnspecified(),
            [typeof(DateTimeOffset)] = zoned.ToDateTimeOffset(),
            [typeof(LocalDate)] = local.Date,
            [typeof(LocalTime)] = local.TimeOfDay,
            [typeof(LocalDateTime)] = local,
            [typeof(Instant)] = SampleInstant,
        };

        var formatters = WithCollisions(BuildFormatterCards(samples));

        return new FormatGalleryViewModel(
            SampleUtc: SampleInstant.ToIso8601(),
            SampleLocal: local.ToDateTimeUnspecified().ToInvariantTimestamp(),
            SampleZone: SampleZone.Id,
            CultureSensitive: formatters.Where(c => !string.Equals(c.EsOutput, c.EnOutput, StringComparison.Ordinal)).ToList(),
            CultureStable: formatters.Where(c => string.Equals(c.EsOutput, c.EnOutput, StringComparison.Ordinal)).ToList(),
            Patterns: BuildPatternCards(samples));
    }

    private static List<FormatterCard> BuildFormatterCards(IReadOnlyDictionary<Type, object> samples)
    {
        var cards = new List<FormatterCard>();

        var byName = typeof(DateFormattingExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(string) && !m.IsSpecialName)
            .Where(m => !IsNullableValueParam(m)) // drop the `DateTime?`/`Instant?` delegators
            .GroupBy(m => m.Name, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var group in byName)
        {
            var chosen = PickInvocable(group, samples);
            if (chosen is null)
                continue;

            var (method, args) = chosen.Value;
            cards.Add(new FormatterCard(
                Name: group.Key,
                InputType: DescribeInput(method),
                EsOutput: Invoke(method, args, Es),
                EnOutput: Invoke(method, args, En),
                SameOutputAs: []));
        }

        return cards;
    }

    private static List<PatternCard> BuildPatternCards(IReadOnlyDictionary<Type, object> samples)
    {
        var cards = new List<PatternCard>();

        var fields = typeof(DateFormattingExtensions)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsInitOnly) // static readonly
            .OrderBy(f => f.Name, StringComparer.Ordinal);

        foreach (var field in fields)
        {
            var patternInterface = field.FieldType.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPattern<>));
            if (patternInterface is null)
                continue;

            var pattern = field.GetValue(null);
            if (pattern is null)
                continue;

            var valueType = patternInterface.GetGenericArguments()[0];
            var patternText = field.FieldType.GetProperty("PatternText")?.GetValue(pattern) as string ?? "";

            var sample = "(no sample)";
            if (samples.TryGetValue(valueType, out var value))
                sample = patternInterface.GetMethod("Format")!.Invoke(pattern, [value]) as string ?? "(null)";

            cards.Add(new PatternCard(field.Name, FriendlyType(valueType), patternText, sample));
        }

        return cards;
    }

    // Two formatters that produce identical text in BOTH shown cultures are redundancy
    // candidates — flag each with the other's name so they can be eyeballed.
    private static List<FormatterCard> WithCollisions(List<FormatterCard> cards) =>
        cards
            .Select(c => c with
            {
                SameOutputAs = cards
                    .Where(o => !string.Equals(o.Name, c.Name, StringComparison.Ordinal)
                        && string.Equals(o.EsOutput, c.EsOutput, StringComparison.Ordinal)
                        && string.Equals(o.EnOutput, c.EnOutput, StringComparison.Ordinal))
                    .Select(o => o.Name)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToList(),
            })
            .ToList();

    private static bool IsNullableValueParam(MethodInfo method)
    {
        var ps = method.GetParameters();
        return ps.Length >= 1
            && ps[0].ParameterType.IsGenericType
            && ps[0].ParameterType.GetGenericTypeDefinition() == typeof(Nullable<>);
    }

    // Prefer the fewest parameters, then the friendliest first-param type.
    private static (MethodInfo method, object?[] args)? PickInvocable(
        IEnumerable<MethodInfo> overloads, IReadOnlyDictionary<Type, object> samples)
    {
        var ordered = overloads
            .OrderBy(m => m.GetParameters().Length)
            .ThenBy(m => InputRank(m.GetParameters()[0].ParameterType));

        foreach (var method in ordered)
        {
            var ps = method.GetParameters();
            if (ps.Length == 0 || !samples.TryGetValue(ps[0].ParameterType, out var first))
                continue;

            var args = new object?[ps.Length];
            args[0] = first;
            var supplied = true;
            for (var i = 1; i < ps.Length; i++)
            {
                if (ps[i].ParameterType == typeof(DateTimeZone))
                    args[i] = SampleZone;
                else
                {
                    supplied = false;
                    break;
                }
            }

            if (supplied)
                return (method, args);
        }

        return null;
    }

    private static int InputRank(Type type)
    {
        var i = Array.IndexOf(InputPreference, type);
        return i < 0 ? int.MaxValue : i;
    }

    private static string Invoke(MethodInfo method, object?[] args, CultureInfo culture)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
        try
        {
            return method.Invoke(null, args) as string ?? "(null)";
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static string DescribeInput(MethodInfo method)
    {
        var ps = method.GetParameters();
        var first = FriendlyType(ps[0].ParameterType);
        return ps.Length > 1 && ps[1].ParameterType == typeof(DateTimeZone) ? $"{first} + tz" : first;
    }

    private static string FriendlyType(Type type) => type.Name;
}
