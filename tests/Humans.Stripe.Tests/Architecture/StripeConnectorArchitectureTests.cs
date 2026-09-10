using AwesomeAssertions;
using Humans.Stripe.Contracts;

namespace Humans.Stripe.Tests.Architecture;

/// <summary>
/// Enforces the §15i connector (API bridge) seam for Stripe
/// (nobodies-collective/Humans#556): consumers depend on <see cref="IStripeService"/> under
/// <c>Contracts/</c>, never on <c>Stripe.net</c> SDK types. One test, below.
/// </summary>
public class StripeConnectorArchitectureTests
{
    // COVERAGE REDUCED (nobodies-collective/Humans#866): the assertion that the hub carried no
    // Stripe.net reference is gone — Humans.Application has no types left to measure. Nothing now
    // enforces "Humans.Stripe is the only production project referencing Stripe.net".

    // COVERAGE REDUCED (nobodies-collective/Humans#866): the matching Humans.Web assertion was
    // dropped rather than have this section's test project take a ProjectReference to Shell to
    // keep one reflection assertion alive. Restore it from a host-level architecture test project.

    [HumansFact]
    public void IStripeService_ExposesNoStripeSdkTypesOnItsPublicSurface()
    {
        var methodTypes = typeof(IStripeService).GetMethods()
            .SelectMany(m => new[] { m.ReturnType }.Concat(m.GetParameters().Select(p => p.ParameterType)));
        var propertyTypes = typeof(IStripeService).GetProperties()
            .Select(p => p.PropertyType);

        var allTypes = methodTypes.Concat(propertyTypes)
            .SelectMany(WalkTypes)
            .Distinct();

        allTypes.Should().NotContain(
            t => (t.Namespace ?? string.Empty).StartsWith("Stripe", StringComparison.Ordinal),
            because: "IStripeService is the bridge — SDK types must stay on the section's Services/ side of the seam (design-rules §15i)");
    }

    // Recursively expose every type referenced by a surface type — unwrapping
    // Nullable<>, arrays/by-ref/pointers, and all generic arguments (at any depth)
    // so a nested leak like Task<List<Stripe.X>> cannot bypass the guard.
    private static IEnumerable<Type> WalkTypes(Type type)
    {
        var seen = new HashSet<Type>();
        var stack = new Stack<Type>();
        stack.Push(type);
        while (stack.Count > 0)
        {
            var popped = stack.Pop();
            var current = Nullable.GetUnderlyingType(popped) ?? popped;
            if (!seen.Add(current)) continue;
            yield return current;

            if (current.HasElementType && current.GetElementType() is { } element)
                stack.Push(element);
            if (current.IsGenericType)
                foreach (var arg in current.GetGenericArguments())
                    stack.Push(arg);
        }
    }
}
