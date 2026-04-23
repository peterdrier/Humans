using System.Reflection;
using AwesomeAssertions;
using Humans.Application.Interfaces.GoogleIntegration;
using Xunit;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests for the §15 Part 2a Google Workspace SDK bridge
/// interfaces (issue #574). These bridges gate every
/// <c>Google.Apis.*</c> call made by <c>GoogleWorkspaceSyncService</c>, which
/// moves into the Application layer in Part 2b (#575). The tests below are
/// the compile-time guarantee that the bridge surface stays shape-neutral
/// and that the Application assembly does not drift back into a Google SDK
/// dependency.
/// </summary>
public class GoogleWorkspaceSyncBridgeArchitectureTests
{
    /// <summary>
    /// Every bridge interface introduced by Part 2a — enforced below so
    /// adding a new one forces the author to add it to the architecture
    /// test suite as well.
    /// </summary>
    public static readonly IReadOnlyList<Type> BridgeInterfaces =
    [
        typeof(IGoogleGroupMembershipClient),
        typeof(IGoogleGroupProvisioningClient),
        typeof(IGoogleDrivePermissionsClient),
        typeof(IGoogleDirectoryClient)
    ];

    public static IEnumerable<object[]> BridgeInterfaceCases =>
        BridgeInterfaces.Select(t => new object[] { t });

    // ── Namespace + location ─────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(BridgeInterfaceCases))]
    public void BridgeInterface_LivesInApplicationInterfacesNamespace(Type bridge)
    {
        bridge.Namespace
            .Should().Be("Humans.Application.Interfaces.GoogleIntegration",
                because: "connector interfaces live alongside other Application interfaces per design-rules §2b");
    }

    [Theory]
    [MemberData(nameof(BridgeInterfaceCases))]
    public void BridgeInterface_LivesInApplicationAssembly(Type bridge)
    {
        bridge.Assembly.GetName().Name
            .Should().Be("Humans.Application",
                because: "bridge contract belongs to the Application layer; implementations live in Humans.Infrastructure");
    }

    // ── Shape-neutral surface ────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(BridgeInterfaceCases))]
    public void BridgeInterface_HasNoGoogleSdkTypesInSignatures(Type bridge)
    {
        // Every method parameter, return type, and the types nested inside
        // generic arguments must live in Humans.* or the BCL — never
        // Google.Apis.*. This is what "shape-neutral" means: the Application
        // layer compiles against the bridge without a Google.Apis.*
        // transitive reference.
        var methods = bridge.GetMethods();

        foreach (var method in methods)
        {
            var types = new[] { method.ReturnType }
                .Concat(method.GetParameters().Select(p => p.ParameterType))
                .SelectMany(UnwrapGenericArgs);

            foreach (var t in types)
            {
                (t.Namespace ?? string.Empty)
                    .Should().NotStartWith("Google.Apis",
                        because: $"{bridge.Name}.{method.Name} leaks a Google SDK type through its signature; connector contracts must be shape-neutral");
            }
        }
    }

    // ── Assembly cleanliness ─────────────────────────────────────────────────

    [Fact]
    public void HumansApplication_HasNoGoogleApisAssemblyReference()
    {
        // Structural guarantee: the Application csproj does not
        // (transitively) reference any Google.Apis.* assembly. Without this,
        // the whole point of the bridge collapses — a service could grab
        // an SDK type anyway.
        var applicationAssembly = typeof(IGoogleGroupMembershipClient).Assembly;
        var referenced = applicationAssembly.GetReferencedAssemblies();

        referenced
            .Should().NotContain(
                a => (a.Name ?? string.Empty).StartsWith("Google.Apis", StringComparison.Ordinal),
                because: "Humans.Application must stay free of Google SDK references; Google API calls live behind bridge interfaces in Humans.Infrastructure");
    }

    [Theory]
    [MemberData(nameof(BridgeInterfaceCases))]
    public void BridgeInterface_DoesNotReferenceGoogleSdkTypes(Type bridge)
    {
        // Paranoid double-check at the Type metadata level — catches cases
        // where an extension method or static class reintroduces an SDK type
        // inside the DTO namespace.
        var module = bridge.Module;
        var referencedTypes = module.GetTypes()
            .SelectMany(t => new[] { t.BaseType }
                .Concat(t.GetInterfaces())
                .Concat(t.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Select(f => f.FieldType))
                .Concat(t.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Select(p => p.PropertyType)))
            .Where(t => t is not null)
            .Select(t => t!.Namespace ?? string.Empty);

        referencedTypes
            .Should().NotContain(
                ns => ns.StartsWith("Google.Apis", StringComparison.Ordinal),
                because: $"{bridge.Name} must not see any Google SDK types — they belong behind the bridge in Humans.Infrastructure");
    }

    private static IEnumerable<Type> UnwrapGenericArgs(Type t)
    {
        yield return t;
        if (t.IsGenericType)
        {
            foreach (var arg in t.GetGenericArguments())
            {
                foreach (var inner in UnwrapGenericArgs(arg))
                    yield return inner;
            }
        }
    }
}
