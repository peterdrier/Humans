using AwesomeAssertions;
using Humans.Stripe.Contracts;

namespace Humans.Stripe.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15i connector (API bridge) pattern for
/// the Stripe integration (nobodies-collective/Humans#556).
///
/// <para>
/// Stripe is an external vendor connector and, since nobodies-collective/Humans#866
/// (G5 lane 4b-2a), a section of its own: <c>Humans.Stripe</c>. Consumers depend on the
/// <see cref="IStripeService"/> abstraction under <c>Contracts/</c> only, never on
/// <c>Stripe.net</c> SDK types. <c>Humans.Stripe</c> is the only production assembly that
/// imports the <c>Stripe</c> namespace. Stripe owns no database tables — Stripe fee values
/// land on <c>TicketOrder</c> (Tickets) and <c>Payment</c> (Store), written through those
/// sections' own repository paths.
/// </para>
/// <para>
/// Two tests remain: one fails if <c>Stripe.net</c> is pulled into the Application assembly,
/// the other if an SDK type appears on the <see cref="IStripeService"/> surface. The Web
/// assembly is no longer checked — see the COVERAGE REDUCED note below.
/// </para>
/// </summary>
public class StripeConnectorArchitectureTests
{
    // COVERAGE REDUCED (nobodies-collective/Humans#866, G5 lane 5c):
    // HumansApplicationAssembly_HasNoReferenceToStripeNet is gone. It asserted the hub pulled in no
    // Stripe.net reference; lane 5c emptied Humans.Application of every type, so the assertion had
    // nothing left to measure. Re-pointing it at Base would be widening a guardrail during a move,
    // which the batch's ruling 1 forbids. IStripeService_ExposesNoStripeSdkTypes below still holds
    // the seam that matters.

    // COVERAGE REDUCED (nobodies-collective/Humans#866, G5 lane 4b-2a): the sibling
    // HumansWebAssembly_HasNoReferenceToStripeNet assertion did not come across. It
    // anchored on typeof(Humans.Web.Controllers.AboutController), and a section test
    // project taking a ProjectReference to Shell to keep one reflection assertion alive is
    // a worse trade than the assertion is worth. Restore it from a host-level architecture
    // test project once Humans.Web becomes Humans.Host (design 2026-08-14, phase 4b-iv).

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
