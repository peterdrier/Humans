using System.Reflection;
using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Users.Services;
using Humans.Users.Tests.Infrastructure;
using Humans.Users.Data;

namespace Humans.Users.Tests.Services;

public sealed class DependencyCycleResolutionTests : ServiceTestHarness
{
    /// <summary>
    /// Fails fast instead of hanging at first request: <c>ValidateOnBuild</c> misses cycles routed through
    /// <c>sp => sp.GetRequiredService&lt;Impl&gt;()</c> forwarders. Both role markers are swept (the role axis is
    /// exclusive); lazy edges (IServiceProvider, Lazy, Func, IEnumerable) are not followed, as they break cycles in MS DI.
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

        // Interface → EVERY in-scope implementer, not the "IFoo → Foo" name match: read interfaces
        // are forwarders onto Caching* decorators, so a name match drops exactly the edges this guard
        // exists for. A surplus edge can at worst over-report; a missing one hides a real cycle.
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
                    // Drop the self-edge a decorator creates by injecting the interface it
                    // implements: it wraps a separately-registered concrete, not a cycle.
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
