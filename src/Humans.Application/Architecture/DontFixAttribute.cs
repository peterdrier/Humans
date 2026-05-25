namespace Humans.Application.Architecture;

/// <summary>
/// Marks a class as an <b>intentional, permanent</b> exception to an architecture
/// rule. Unlike <see cref="GrandfatheredAttribute"/> — which is a TODO that should
/// be refactored away and is fair game for automated tech-debt passes — a
/// <c>[DontFix]</c> class is meant to stay as-is. Automated agents must treat it as
/// off-limits and never "fix" it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Provenance is the defining property.</b> Agents may add
/// <see cref="GrandfatheredAttribute"/> when they find or create a violation they
/// intend to clean up. Agents must <b>never</b> add <c>[DontFix]</c>: it is applied
/// by Peter only. If a rule seems wrong for a given case, an agent reports it — it
/// does not tag the class to silence the rule. This is a governance rule, not a
/// compiler-enforced one (an analyzer cannot see who authored an attribute), so its
/// strength is the clarity of that instruction.
/// </para>
///
/// <para>
/// This is the single sanctioned exception to
/// <c>memory/process/no-analyzer-suppressions.md</c>. It is not a substitute for
/// <c>#pragma warning disable</c>, <c>[SuppressMessage]</c>, <c>.editorconfig</c>
/// per-file severity blocks, or any centralised allowlist — those remain forbidden.
/// </para>
///
/// <para>
/// When a rule is encoded as an analyzer, the analyzer should suppress its diagnostic
/// entirely (emit nothing) for a class carrying <c>[DontFix]</c> with the matching
/// <see cref="RuleId"/> — there is no TODO to keep visible. <see cref="RuleId"/> is
/// optional because a permanent exception may pre-date its analyzer.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class DontFixAttribute(string reason, string? ruleId = null, string? since = null)
    : Attribute
{
    public string Reason { get; } = reason;
    public string? RuleId { get; } = ruleId;
    public string? Since { get; } = since;
}
