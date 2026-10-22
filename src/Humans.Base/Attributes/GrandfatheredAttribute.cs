namespace Humans.Base.Attributes;

/// <summary>
/// Marks a class (or, for method-level rules like HUM0031, a method) as a
/// known, pre-existing violator of an architecture analyzer rule. The analyzer
/// downgrades its diagnostic from <c>Error</c> to <c>Warning</c> for any
/// declaration carrying this attribute with the matching <c>RuleId</c>, so the
/// build stays green while the migration work is in flight. Every use is a
/// TODO — refactor the code to comply with the rule, then delete the attribute
/// in the same commit.
/// </summary>
/// <remarks>
/// <para>
/// Per-declaration attributes only: never analyzer-internal allowlists, <c>.editorconfig</c> severity blocks or any
/// centralised list, which conflict on every merge and pull cleanup away from the violating code.
/// </para>
/// <para>
/// <code>
/// [Grandfathered(ruleId: "HUM0009", justification: "Pending migration to repository pattern.",
///     since: "2026-05-12", issueRef: "nobodies-collective/Humans#701")]
/// </code>
/// <c>since</c> and <c>issueRef</c> are informational; the analyzer branches only on <see cref="RuleId"/>.
/// <see cref="Scope"/> narrows the grandfather to one sub-target (for HUM0025, a DbSet name, one attribute per
/// shared table); rules without a scope leave it null.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class GrandfatheredAttribute(string ruleId, string justification, string since, string issueRef, string? scope = null)
    : Attribute
{
    public string RuleId { get; } = ruleId;
    public string Justification { get; } = justification;
    public string Since { get; } = since;
    public string IssueRef { get; } = issueRef;
    public string? Scope { get; } = scope;
}
