using AwesomeAssertions;
using Humans.Stripe.Contracts;
using Humans.Stripe.Services;

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
/// These tests fail loudly if a future change pulls <c>Stripe.net</c> into the Application
/// or Web assemblies, or leaks SDK types onto the <see cref="IStripeService"/> surface.
/// </para>
/// </summary>
public class StripeConnectorArchitectureTests
{
    [HumansFact]
    public void IStripeService_LivesInTheSectionsContractsNamespace()
    {
        typeof(IStripeService).Namespace
            .Should().Be("Humans.Stripe.Contracts",
                because: "the connector's outward surface is its Contracts/ folder — HUM0034 allows public section types nowhere else (design-rules §15i)");
    }

    [HumansFact]
    public void HumansApplicationAssembly_HasNoReferenceToStripeNet()
    {
        // Anchored on a type that stays in Humans.Application. It used to read
        // typeof(Humans.Application.CacheKeys).Assembly, which G5 lane 3a-1 moved to
        // Humans.Interfaces with its namespace preserved — the assertion would have
        // silently started measuring a different (and vacuously clean) assembly.
        // DashboardService is a concrete Humans.Application service with no scheduled
        // move in phase 3.
        var applicationAssembly = typeof(Humans.Application.Services.Dashboard.DashboardService).Assembly;
        applicationAssembly.GetName().Name.Should().Be("Humans.Application",
            because: "an anchor whose type leaves this assembly would silently retarget this test onto the wrong assembly instead of failing");

        var referenced = applicationAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        referenced.Should().NotContain(
            name => name.StartsWith("Stripe", StringComparison.Ordinal),
            because: "the hub must not reference the Stripe.net SDK — the connector is a section of its own (design-rules §15i)");
    }

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

    [HumansFact]
    public void StripeServiceImplementation_IsInternalToTheSection()
    {
        var impl = typeof(StripeService);

        impl.Namespace
            .Should().Be("Humans.Stripe.Services",
                because: "the Stripe.net-using implementation stays behind the seam — only Contracts/ crosses it (design-rules §15i)");
        impl.IsPublic
            .Should().BeFalse(
                because: "section types are internal by default; only Contracts/ members and Section are public (HUM0034)");
    }
}
