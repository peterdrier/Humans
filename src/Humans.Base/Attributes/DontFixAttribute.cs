namespace Humans.Base.Attributes;

/// <summary>
/// Marks a class as an <b>intentional, permanent</b> exception to an architecture rule. Unlike
/// <see cref="GrandfatheredAttribute"/>, which is a TODO, automated agents must never "fix" it.
/// </summary>
/// <remarks>
/// Applied by Peter only: agents never add <c>[DontFix]</c>, and report a rule that seems wrong instead. It is the
/// single sanctioned exception to <c>memory/process/no-analyzer-suppressions.md</c>, not a substitute for pragmas,
/// <c>[SuppressMessage]</c> or allowlists. An analyzer emits nothing for a class carrying it with the matching
/// <see cref="RuleId"/>, which is optional because a permanent exception may pre-date its analyzer.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class DontFixAttribute(string reason, string? ruleId = null, string? since = null)
    : Attribute
{
    public string Reason { get; } = reason;
    public string? RuleId { get; } = ruleId;
    public string? Since { get; } = since;
}
