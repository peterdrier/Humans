namespace Humans.Application.Architecture;

/// <summary>
/// Per-type public-instance method-count budget. Decorate a type
/// (typically an interface) with <c>[SurfaceBudget(N)]</c> where <c>N</c>
/// equals the current directly-declared public-instance ordinary-method
/// count. The <c>SurfaceBudgetAnalyzer</c> (HUM0015 / HUM0016) fails the
/// build whenever the actual count drifts away from <c>N</c>.
/// </summary>
/// <remarks>
/// A <b>consolidation ratchet</b>: budgeted types should shrink over time.
/// <b>Owner-applied only</b> — agents NEVER add this attribute or suggest
/// adding it; they only keep an already-present <c>N</c> accurate. It lives
/// predominantly on read interfaces (currently the <c>I…ServiceRead</c> types).
/// Full rule: <c>memory/code/surface-budget-owner-applied.md</c>.
///
/// <para>When editing a type that already carries it:</para>
/// <list type="bullet">
///   <item><b>No raises</b> — add a method only by removing one from the same
///     type in the same PR (net delta &lt;= 0).</item>
///   <item><b>No sub-interface/partial splits</b> to park methods under a fresh budget.</item>
///   <item><b>No "2 methods → 1 bag-of-flags" tricks</b> — count drops, surface grows.</item>
///   <item><b>On net-negative, lower <c>N</c> to the exact new count</b> (HUM0016 fires on slack).</item>
/// </list>
///
/// <para>
/// Counting (mirrored in <c>SurfaceBudgetAnalyzer</c>): directly-declared
/// public-instance ordinary methods only — not private/internal/protected/
/// static, not accessors/indexers/events, not inherited members.
/// </para>
/// </remarks>
[AttributeUsage(
    AttributeTargets.Interface | AttributeTargets.Class | AttributeTargets.Struct,
    AllowMultiple = false,
    Inherited = false)]
public sealed class SurfaceBudgetAttribute(int methodCount) : Attribute
{
    public int MethodCount { get; } = methodCount;
}
