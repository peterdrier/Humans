using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Humans.Analyzers.Internal;

internal static class AssignmentTarget
{
    /// <summary>
    /// Returns the assignment target operation for any assignment-shaped
    /// operation: simple (<c>x = y</c>), compound (<c>x += y</c>,
    /// <c>x |= y</c>, etc.), and coalesce (<c>x ??= y</c>). Increment /
    /// decrement / deconstruction targets are handled at their own operation
    /// kinds — keep them out of this helper to avoid surprising the caller.
    /// </summary>
    public static IOperation? From(IOperation op) => op switch
    {
        ISimpleAssignmentOperation s => s.Target,
        ICompoundAssignmentOperation c => c.Target,
        ICoalesceAssignmentOperation k => k.Target,
        _ => null,
    };
}
