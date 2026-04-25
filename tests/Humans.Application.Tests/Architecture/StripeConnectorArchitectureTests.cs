using AwesomeAssertions;
using Humans.Application.Interfaces;
using Xunit;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15i connector (API bridge) pattern for
/// the Stripe integration (issue #556).
///
/// <para>
/// Stripe is an external connector: the Application layer depends on the
/// <see cref="IStripeService"/> abstraction only, never on <c>Stripe.net</c>
/// SDK types. The concrete <c>StripeService</c> implementation lives in
/// <c>Humans.Infrastructure.Services</c> and is the only project that
/// imports the <c>Stripe</c> namespace. Stripe does not own any database
/// tables — Stripe fee values land on <c>TicketOrder</c> (Tickets section),
/// written through the Tickets-owned repository path.
/// </para>
/// <para>
/// These tests fail loudly if a future change accidentally pulls
/// <c>Stripe.net</c> into the Application assembly, leaks SDK types onto the
/// <see cref="IStripeService"/> surface, or drags the interface back into
/// Infrastructure.
/// </para>
/// </summary>
public class StripeConnectorArchitectureTests
{
    [HumansFact]
    public void IStripeService_LivesInHumansApplicationInterfacesNamespace()
    {
        typeof(IStripeService).Namespace
            .Should().Be("Humans.Application.Interfaces",
                because: "connector interfaces live in Humans.Application so Application-layer services depend only on the abstraction (design-rules §15i)");
    }

    [HumansFact]
    public void HumansApplicationAssembly_HasNoReferenceToStripeNet()
    {
        var applicationAssembly = typeof(IStripeService).Assembly;

        var referenced = applicationAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        referenced.Should().NotContain(
            name => name.StartsWith("Stripe", StringComparison.Ordinal),
            because: "Humans.Application must not reference the Stripe.net SDK — the connector implementation lives in Infrastructure (design-rules §15i)");
    }

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
            because: "IStripeService is the bridge — SDK types must stay on the Infrastructure side of the seam (design-rules §15i)");
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

    [HumansFact]
    public void StripeServiceImplementation_LivesInInfrastructureServicesNamespace()
    {
        var impl = typeof(IStripeService).Assembly
            .GetReferencedAssemblies()
            .Select(a => AppDomain.CurrentDomain.Load(a))
            .Concat([typeof(Humans.Infrastructure.Services.StripeService).Assembly])
            .SelectMany(a => a.GetExportedTypes())
            .Single(t => string.Equals(t.Name, "StripeService", StringComparison.Ordinal)
                         && typeof(IStripeService).IsAssignableFrom(t));

        impl.Namespace
            .Should().Be("Humans.Infrastructure.Services",
                because: "the Stripe.net-using implementation stays in Infrastructure — only the abstraction crosses the seam (design-rules §15i)");
    }
}
