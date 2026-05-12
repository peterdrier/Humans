namespace Humans.Application.Architecture;

/// <summary>
/// Marks a class as a known, pre-existing violator of an architecture analyzer
/// rule. The analyzer downgrades its diagnostic from <c>Error</c> to
/// <c>Warning</c> for any type carrying this attribute with the matching
/// <c>RuleId</c>, so the build stays green while the migration work is in
/// flight. Every use is a TODO — refactor the class to comply with the rule,
/// then delete the attribute in the same commit.
/// </summary>
/// <remarks>
/// <para>
/// This is the project's standard mechanism for grandfathering rule violations.
/// Per-file attributes only — <b>do not</b> introduce analyzer-internal
/// allowlists, <c>.editorconfig</c> per-file severity blocks, or any other
/// centralised maintained list. Centralised lists conflict on every merge and
/// pull cleanup work away from the violating code.
/// </para>
///
/// <para>
/// Add to a class:
/// <code>
/// [Grandfathered(
///     ruleId: "HUM0009",
///     justification: "Pending migration to repository pattern.",
///     since: "2026-05-12",
///     issueRef: "nobodies-collective/Humans#701")]
/// public class ProcessEmailOutboxJob { … }
/// </code>
/// </para>
///
/// <para>
/// The <c>since</c> date lets reviewers see how long the grandfather has been
/// in place. The <c>issueRef</c> is the umbrella or per-class tracking issue.
/// Both are informational — the analyzer only branches on the presence of the
/// attribute with the matching <see cref="RuleId"/>.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class GrandfatheredAttribute : Attribute
{
    public GrandfatheredAttribute(
        string ruleId,
        string justification,
        string since,
        string issueRef)
    {
        RuleId = ruleId;
        Justification = justification;
        Since = since;
        IssueRef = issueRef;
    }

    public string RuleId { get; }
    public string Justification { get; }
    public string Since { get; }
    public string IssueRef { get; }
}
