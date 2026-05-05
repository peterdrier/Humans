using System.Text.RegularExpressions;
using Humans.Application.Tests.Architecture.Ratchet;
using Humans.Testing;

namespace Humans.Application.Tests.Architecture.Rules;

/// <summary>
/// Ratcheted rule: a section's EF model joins only to its own tables.
/// Cross-section linkage must be a bare <c>Guid</c> column — no
/// <c>HasOne&lt;X&gt;()</c>/<c>HasMany&lt;X&gt;()</c> to entities owned by
/// other sections, no nav properties across sections.
///
/// Source rule: <c>memory/architecture/no-cross-section-ef-joins.md</c>.
///
/// Detection (mechanical, conservative):
/// - Each EF configuration lives under <c>src/Humans.Infrastructure/Data/Configurations/&lt;Section&gt;/</c>.
///   The folder name is the configured entity's section.
/// - For each configuration, parse the entity it configures (the
///   <c>T</c> in <c>IEntityTypeConfiguration&lt;T&gt;</c>) and every other
///   entity referenced via <c>HasOne&lt;Y&gt;</c>, <c>HasMany&lt;Y&gt;</c>,
///   <c>HasOne(b =&gt; b.Foo)</c>-style nav references.
/// - Build a map from entity name → owning-section by walking every
///   configuration file once.
/// - A reference is cross-section if the referenced entity's owning section
///   differs from the configuration's section.
///
/// Configurations sitting directly in <c>Configurations/</c> (no section
/// subfolder) are treated as their own per-entity section and excluded from
/// the rule (their sectional ownership is not unambiguously declared by the
/// folder layout).
/// </summary>
public class NoCrossSectionEfJoinsRule
{
    private const string BaselinePath =
        "tests/Humans.Application.Tests/Architecture/Baselines/NoCrossSectionEfJoins.baseline.txt";

    [HumansFact]
    public void No_new_cross_section_EF_joins()
    {
        var repoRoot = RatchetTestRunner.LocateRepoRoot();
        var violations = ScanConfigurations(repoRoot);
        RatchetTestRunner.Run("NoCrossSectionEfJoins", BaselinePath, violations);
    }

    // HasOne<Type>() / HasMany<Type>() — generic typed.
    private static readonly Regex GenericNavRegex = new(
        @"\.(?<op>HasOne|HasMany)\s*<\s*(?<type>[A-Z]\w+)\s*>\s*\(",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    // HasOne(x => x.Property) / HasMany(x => x.Property) — lambda nav.
    private static readonly Regex LambdaNavRegex = new(
        @"\.(?<op>HasOne|HasMany)\s*\(\s*\w+\s*=>\s*\w+\.(?<prop>[A-Z]\w+)",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    // class XxxConfiguration : IEntityTypeConfiguration<EntityName>
    private static readonly Regex ConfiguredEntityRegex = new(
        @"IEntityTypeConfiguration\s*<\s*(?<entity>[A-Z]\w+)\s*>",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    internal static IEnumerable<string> ScanConfigurations(string repoRoot)
    {
        var configRoot = Path.Combine(repoRoot, "src", "Humans.Infrastructure", "Data", "Configurations");
        if (!Directory.Exists(configRoot)) yield break;

        // Pass 1: build entity → section map.
        var entitySection = new Dictionary<string, string>(StringComparer.Ordinal);
        var fileSection = new Dictionary<string, string>(StringComparer.Ordinal);
        var fileEntity = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(configRoot, "*Configuration.cs", SearchOption.AllDirectories))
        {
            var section = SectionFromPath(configRoot, path);
            if (section is null) continue; // root-level config, skip ownership claim
            var content = File.ReadAllText(path);
            var entityMatch = ConfiguredEntityRegex.Match(content);
            if (!entityMatch.Success) continue;
            var entity = entityMatch.Groups["entity"].Value;

            // First-write wins; if two sections both declare the same entity,
            // the first one we see "owns" it. (The rule itself precludes that
            // ambiguity in practice — each entity has one owning configuration.)
            if (!entitySection.ContainsKey(entity))
                entitySection[entity] = section;
            fileSection[path] = section;
            fileEntity[path] = entity;
        }

        // Pass 2: scan each file for cross-section references.
        foreach (var path in Directory.EnumerateFiles(configRoot, "*Configuration.cs", SearchOption.AllDirectories))
        {
            if (!fileSection.TryGetValue(path, out var thisSection)) continue;
            var content = File.ReadAllText(path);
            var rel = RatchetTestRunner.ToRelativePath(repoRoot, path);
            // Per-(file, key) ordinal so two refs with the same shape in the
            // same file remain distinct without line numbers.
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var match in GenericNavRegex.Matches(content).Cast<Match>())
            {
                var op = match.Groups["op"].Value;
                var referenced = match.Groups["type"].Value;
                if (!entitySection.TryGetValue(referenced, out var refSection)) continue;
                if (string.Equals(refSection, thisSection, StringComparison.Ordinal)) continue;
                var key = $"{op}<{referenced}>({thisSection}->{refSection})";
                counts.TryGetValue(key, out var n);
                counts[key] = ++n;
                var line = RatchetTestRunner.LineNumberAt(content, match.Index);
                yield return $"{rel}:{key}#{n} # L{line}";
            }

            foreach (var match in LambdaNavRegex.Matches(content).Cast<Match>())
            {
                var op = match.Groups["op"].Value;
                var navProperty = match.Groups["prop"].Value;
                // Resolve nav property to an entity by looking up the property
                // on the configured entity's class. We approximate by matching
                // the property name to a configured entity name when it equals
                // an entity name (common case: HasOne(x => x.User) → User).
                if (!entitySection.TryGetValue(navProperty, out var refSection)) continue;
                if (string.Equals(refSection, thisSection, StringComparison.Ordinal)) continue;
                var key = $"{op}(=>.{navProperty})({thisSection}->{refSection})";
                counts.TryGetValue(key, out var n);
                counts[key] = ++n;
                var line = RatchetTestRunner.LineNumberAt(content, match.Index);
                yield return $"{rel}:{key}#{n} # L{line}";
            }
        }
    }

    private static string? SectionFromPath(string configRoot, string filePath)
    {
        var rel = Path.GetRelativePath(configRoot, filePath);
        var parts = rel.Replace('\\', '/').Split('/');
        // parts.Length == 1 → file directly under Configurations/, no section.
        return parts.Length >= 2 ? parts[0] : null;
    }
}
