using System.Reflection;
using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Users.Services;
using Humans.Users.Tests.Infrastructure;
using Humans.Users.Data;

namespace Humans.Users.Tests.Services;

public sealed class DependencyCycleResolutionTests : ServiceTestHarness
{
    /// <summary>
    /// Generic cycle guard. Scans every concrete class implementing
    /// <see cref="IApplicationService"/> or <see cref="IOrchestrator"/> across
    /// the Humans assemblies — the role axis is exclusive, so both markers must
    /// be swept or reclassifying a service silently drops it from the graph —
    /// maps each
    /// interface ctor parameter to every in-scope concrete implementation of that
    /// interface, and DFS-detects cycles. Edges through lazy escape hatches
    /// (<see cref="IServiceProvider"/>, <see cref="Lazy{T}"/>, <see cref="Func{T}"/>,
    /// <see cref="IEnumerable{T}"/>) are deliberately not followed — those defer
    /// resolution out of the ctor and break cycles in MS DI.
    ///
    /// This test fails fast at build time, instead of hanging at first request
    /// like the original <c>IOnboardingEligibilityQuery</c> incident, by
    /// inspecting the graph directly rather than relying on
    /// <c>ServiceProviderOptions.ValidateOnBuild</c>, which misses cycles routed
    /// through <c>sp => sp.GetRequiredService&lt;ConcreteImpl&gt;()</c>
    /// forwarder factories.
    /// </summary>
    [HumansFact]
    public void NoCircularConstructorDependencies_AcrossApplicationServices()
    {
        var assemblies = new[]
        {
            typeof(UserService).Assembly,
            typeof(UsersDbContext).Assembly,
            typeof(Humans.Web.Controllers.HomeController).Assembly,
        };

        var concreteServices = assemblies
            .SelectMany(SafeGetTypes)
            .Where(t => t.IsClass && !t.IsAbstract && !t.IsGenericTypeDefinition)
            .Where(t => typeof(IApplicationService).IsAssignableFrom(t)
                        || typeof(IOrchestrator).IsAssignableFrom(t))
            .ToHashSet();

        // Interface → EVERY concrete service implementing it, restricted to the
        // types collected above so external implementations don't pollute the
        // graph.
        //
        // This used to key on the "IFoo → Foo" naming convention, which silently
        // dropped every cross-section read interface: there is no `UserServiceRead`
        // class, because `IUserServiceRead` (and ITeamServiceRead / ICampServiceRead /
        // IEventServiceRead) is registered as a forwarder factory onto the matching
        // `Caching*Service` decorator. Any edge through a read interface therefore
        // vanished, so a cycle routed via a decorator resolved fine here and blew up
        // only in MS DI — the exact failure mode this guard exists to prevent.
        //
        // Over-approximating (all implementers rather than one) is the safe direction
        // for a cycle guard: a missing edge hides a real cycle, whereas a surplus edge
        // can at worst report one that DI's single chosen implementation wouldn't hit.
        var implsByInterface = new Dictionary<Type, HashSet<Type>>();
        foreach (var concrete in concreteServices)
        {
            foreach (var iface in concrete.GetInterfaces())
            {
                if (!iface.Name.StartsWith("I", StringComparison.Ordinal)) continue;
                if (!implsByInterface.TryGetValue(iface, out var impls))
                    implsByInterface[iface] = impls = [];
                impls.Add(concrete);
            }
        }

        var edges = new Dictionary<Type, HashSet<Type>>();
        foreach (var concrete in concreteServices)
        {
            var ctor = concrete.GetConstructors()
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();
            if (ctor is null) continue;

            var deps = new HashSet<Type>();
            foreach (var p in ctor.GetParameters())
            {
                var pt = p.ParameterType;
                if (IsLazyEscapeHatch(pt)) continue;
                if (pt.IsInterface && implsByInterface.TryGetValue(pt, out var impls))
                {
                    // Drop the self-edge a decorator creates by injecting the very
                    // interface it implements — `CachingUserService(IUserService inner)`
                    // is the decorator pattern wrapping the separately-registered
                    // concrete, not a cycle.
                    foreach (var impl in impls)
                        if (impl != concrete)
                            deps.Add(impl);
                }
                else if (concreteServices.Contains(pt))
                {
                    deps.Add(pt);
                }
            }
            edges[concrete] = deps;
        }

        var state = new Dictionary<Type, int>();
        var cycles = new List<List<Type>>();
        foreach (var node in edges.Keys)
        {
            DfsForCycle(node, edges, state, [], cycles);
        }

        cycles.Should().BeEmpty(
            "constructor dependencies between IApplicationService/IOrchestrator implementations must form a DAG — " +
            "every edge in a cycle is a real ctor injection that MS DI will fail to resolve at first " +
            "request and (in some forwarder-factory configurations) hang instead of throw. Break cycles " +
            "by relocating the predicate/write to its rightful owner, or as a last resort by switching " +
            "one side to IServiceProvider/Lazy<T> lookup with a comment explaining why the inversion " +
            "wasn't viable. Cycles found:\n" +
            string.Join("\n", cycles.Select(c => "  " + string.Join(" -> ", c.Select(t => t.Name)))));
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }

    private static bool IsLazyEscapeHatch(Type t)
    {
        if (t == typeof(IServiceProvider)) return true;
        if (!t.IsGenericType) return false;
        var def = t.GetGenericTypeDefinition();
        return def == typeof(Lazy<>) || def == typeof(Func<>) || def == typeof(IEnumerable<>);
    }

    private static void DfsForCycle(
        Type node,
        IDictionary<Type, HashSet<Type>> edges,
        IDictionary<Type, int> state,
        List<Type> path,
        List<List<Type>> cycles)
    {
        if (state.TryGetValue(node, out var s))
        {
            if (s == 1)
            {
                var start = path.IndexOf(node);
                if (start >= 0)
                {
                    var cycle = path.GetRange(start, path.Count - start);
                    cycle.Add(node);
                    cycles.Add(cycle);
                }
            }
            return;
        }
        state[node] = 1;
        path.Add(node);
        if (edges.TryGetValue(node, out var nexts))
        {
            foreach (var next in nexts)
            {
                DfsForCycle(next, edges, state, path, cycles);
            }
        }
        path.RemoveAt(path.Count - 1);
        state[node] = 2;
    }
}
