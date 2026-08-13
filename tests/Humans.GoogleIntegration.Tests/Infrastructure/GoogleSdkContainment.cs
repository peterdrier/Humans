using System.Reflection;
using AwesomeAssertions;

namespace Humans.GoogleIntegration.Tests.Infrastructure;

/// <summary>
/// The section's Google-SDK containment boundary, and the restated form of four assertions
/// that used to be about assemblies.
/// </summary>
/// <remarks>
/// Before the G5 move (nobodies-collective/Humans#866) the connector interfaces lived in
/// <c>Humans.Application</c> and their implementations in <c>Humans.Infrastructure</c>, so
/// "no <c>Google.Apis.*</c> reference on this assembly" and "no SDK type anywhere in this
/// module" were true statements that meant something. Both halves are in
/// <c>Humans.GoogleIntegration</c> now, so an assembly-level assertion either fails or —
/// worse, written as a "does not contain" — keeps passing while asserting nothing
/// (G5-SECTION-TEMPLATE.md step 11). The boundary is a namespace: everything that names a
/// <c>Google.Apis.*</c> type lives under <see cref="ConnectorNamespace"/>, and the section's
/// business logic under <see cref="ServiceNamespace"/> sees only the shape-neutral
/// interfaces.
/// </remarks>
internal static class GoogleSdkContainment
{
    /// <summary>The section's business-logic layer — must never name an SDK type.</summary>
    internal const string ServiceNamespace = "Humans.GoogleIntegration.Services";

    /// <summary>The connector layer — the only place <c>Google.Apis.*</c> may appear.</summary>
    internal const string ConnectorNamespace = "Humans.GoogleIntegration.Services.Workspace";

    private const BindingFlags AllMembers =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// Asserts that no constructor parameter, method signature, property or field of
    /// <paramref name="type"/> names a <c>Google.Apis.*</c> type. Scoped to the type rather
    /// than its module, which is what makes it survive living in the same assembly as the
    /// connectors.
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
