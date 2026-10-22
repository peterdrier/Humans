using System.Reflection;
using AwesomeAssertions;

namespace Humans.GoogleIntegration.Tests.Infrastructure;

/// <summary>
/// The SDK boundary is a namespace, not an assembly: both connector halves live in
/// <c>Humans.GoogleIntegration</c>, so an assembly-level assertion would pass vacuously.
/// </summary>
internal static class GoogleSdkContainment
{
    /// <summary>The section's business-logic layer — must never name an SDK type.</summary>
    internal const string ServiceNamespace = "Humans.GoogleIntegration.Services";

    /// <summary>The connector layer — the only place <c>Google.Apis.*</c> may appear.</summary>
    internal const string ConnectorNamespace = "Humans.GoogleIntegration.Services.Workspace";

    private const BindingFlags AllMembers =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// Scoped to the type rather than its module, which is what lets it work in the same assembly
    /// as the connectors.
    /// </summary>
    internal static void AssertNamesNoGoogleSdkType(Type type)
    {
        NamespacesOf(type)
            .Should().NotContain(
                ns => ns.StartsWith("Google.Apis", StringComparison.Ordinal),
                because: $"{type.Name} must not see any Google SDK type — they belong behind the connector interfaces in {ConnectorNamespace}");
    }

    /// <summary>
    /// The same assertion over every type in <see cref="ServiceNamespace"/> (excluding the
    /// nested connector namespace). Catches an SDK type reintroduced by a service the
    /// per-type tests do not name.
    /// </summary>
    internal static void AssertServiceLayerNamesNoGoogleSdkType()
    {
        var serviceTypes = typeof(Section).Assembly.GetTypes()
            .Where(t => string.Equals(t.Namespace, ServiceNamespace, StringComparison.Ordinal))
            .ToList();

        serviceTypes.Should().NotBeEmpty(
            because: "the sweep is anchored on a namespace string; an empty result means it stopped covering anything");

        foreach (var type in serviceTypes)
        {
            AssertNamesNoGoogleSdkType(type);
        }
    }

    private static IEnumerable<string> NamespacesOf(Type type)
    {
        var fromSignatures = type.GetConstructors(AllMembers)
            .SelectMany(c => c.GetParameters().Select(p => p.ParameterType))
            .Concat(type.GetMethods(AllMembers)
                .SelectMany(m => new[] { m.ReturnType }.Concat(m.GetParameters().Select(p => p.ParameterType))))
            .Concat(type.GetProperties(AllMembers).Select(p => p.PropertyType))
            .Concat(type.GetFields(AllMembers).Select(f => f.FieldType))
            .Concat(type.GetInterfaces())
            .Concat(type.BaseType is null ? [] : new[] { type.BaseType });

        return fromSignatures
            .SelectMany(Unwrap)
            .Select(t => t.Namespace ?? string.Empty);
    }

    private static IEnumerable<Type> Unwrap(Type t)
    {
        yield return t;
        if (!t.IsGenericType)
        {
            yield break;
        }

        foreach (var inner in t.GetGenericArguments().SelectMany(Unwrap))
        {
            yield return inner;
        }
    }
}
