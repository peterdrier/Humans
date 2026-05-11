namespace Humans.Application.Architecture;

/// <summary>
/// Per-type public-instance method-count budget. Decorate a type
/// (typically an interface) with <c>[SurfaceBudget(N)]</c> where <c>N</c>
/// equals the current directly-declared public-instance ordinary-method
/// count. The <c>SurfaceBudgetAnalyzer</c> (HUM0015 / HUM0016) fails the
/// build whenever the actual count drifts away from <c>N</c>.
/// </summary>
/// <remarks>
/// This attribute is a <b>consolidation ratchet</b> — the goal is for budgeted
/// types to get smaller over time, not stable, not redistributed.
///
/// <para>Agent rules (strict):</para>
/// <list type="bullet">
///   <item>
///     <b>No raises.</b> Adding a method requires removing one from the SAME
///     type in the SAME PR. Net delta is &lt;= 0.
///   </item>
///   <item>
///     <b>No splits as a workaround.</b> Don't extract a sub-interface (or
///     partial / sibling type) to put methods under a fresh budget — that
///     defeats the consolidation goal.
///   </item>
///   <item>
///     <b>No "replace 2 methods with 1 broader bag-of-flags method" tricks.</b>
///     The count drops but the surface grows.
///   </item>
///   <item>
///     <b>Budgets are tight, not padded.</b> HUM0016 fires when the budget has
///     slack. When net delta is negative, lower the budget number to match the
///     new count exactly.
///   </item>
///   <item>
///     <b>Hit a wall? STOP and ask the repo owner.</b> Only the owner authorizes
///     raises, and only out-of-band — never preemptively in a PR.
///   </item>
/// </list>
///
/// <para>
/// Why: the audit-surface skill kept finding bloat that accrued one
/// "this addition is justified, +1" PR at a time. Every raise had a
/// justification. A split-it escape hatch redistributes the same surface
/// across two budgets with fresh growth runway in each. The mechanism only
/// works when agents cannot reach for either lever on their own initiative.
/// </para>
///
/// <para>
/// In scope: types with a meaningful surface (~10+ methods) where growth would
/// matter. Currently applied to service interfaces only, but the attribute is
/// valid on classes and structs too — pick whatever symbol most accurately
/// represents the surface you want budgeted. Smaller types aren't budgeted —
/// adding the 3rd method to a 2-method type isn't a smell.
/// </para>
///
/// <para>
/// Counting semantics (mirrored exactly in <c>SurfaceBudgetAnalyzer</c>):
/// directly-declared <b>public instance</b> ordinary methods only. Private,
/// internal, protected, and static methods are not counted. Property
/// accessors, indexers, events, and inherited members are not counted.
/// (For interfaces, all members are implicitly public-instance, so this
/// collapses to "directly-declared ordinary methods".)
/// </para>
/// </remarks>
[AttributeUsage(
    AttributeTargets.Interface | AttributeTargets.Class | AttributeTargets.Struct,
    AllowMultiple = false,
    Inherited = false)]
public sealed class SurfaceBudgetAttribute : Attribute
{
    public SurfaceBudgetAttribute(int methodCount) => MethodCount = methodCount;

    public int MethodCount { get; }
}
