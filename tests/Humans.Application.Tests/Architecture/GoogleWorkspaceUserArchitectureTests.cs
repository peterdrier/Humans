using System.Reflection;
using AwesomeAssertions;
using Humans.Application.Interfaces.GoogleIntegration;
using Microsoft.EntityFrameworkCore;
using Xunit;
using GoogleWorkspaceUserService = Humans.Application.Services.GoogleIntegration.GoogleWorkspaceUserService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 pattern for the Google Integration
/// section's <see cref="GoogleWorkspaceUserService"/> — migrated under issue
/// #554 (split from the umbrella PR into an isolated sub-task). The service
/// now lives in <c>Humans.Application.Services.GoogleIntegration</c> and
/// routes all Google SDK calls through
/// <see cref="IWorkspaceUserDirectoryClient"/>. These tests are the compile-
/// time guarantee that the connector boundary does not leak back into the
/// Application project.
/// </summary>
public class GoogleWorkspaceUserArchitectureTests
{
    // ── GoogleWorkspaceUserService ───────────────────────────────────────────

    [Fact]
    public void GoogleWorkspaceUserService_LivesInHumansApplicationServicesGoogleIntegrationNamespace()
    {
        typeof(GoogleWorkspaceUserService).Namespace
            .Should().Be("Humans.Application.Services.GoogleIntegration",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [Fact]
    public void GoogleWorkspaceUserService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(GoogleWorkspaceUserService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext (design-rules §3) — this service has no DB, only a connector");
    }

    [Fact]
    public void GoogleWorkspaceUserService_HasNoDbContextFactoryConstructorParameter()
    {
        var ctor = typeof(GoogleWorkspaceUserService).GetConstructors().Single();
        var factoryParam = ctor.GetParameters()
            .FirstOrDefault(p =>
                p.ParameterType.IsGenericType &&
                p.ParameterType.GetGenericTypeDefinition() == typeof(IDbContextFactory<>));

        factoryParam.Should().BeNull(
            because: "IDbContextFactory belongs behind the repository boundary, not in an Application-layer service");
    }

    [Fact]
    public void GoogleWorkspaceUserService_TakesConnectorClient()
    {
        var ctor = typeof(GoogleWorkspaceUserService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IWorkspaceUserDirectoryClient),
            because: "Google SDK calls go through the shape-neutral connector, not directly from the Application service");
    }

    [Fact]
    public void GoogleWorkspaceUserService_IsSealed()
    {
        typeof(GoogleWorkspaceUserService).IsSealed.Should().BeTrue(
            because: "service implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }

    // ── Application assembly cleanliness ─────────────────────────────────────

    [Fact]
    public void HumansApplicationAssembly_HasNoGoogleApisReferences()
    {
        // The Application project must never transitively pull Google.Apis.*.
        // Scanning referenced assemblies is the compile-time guarantee for this.
        var applicationAssembly = typeof(IGoogleWorkspaceUserService).Assembly;
        var referencedNames = applicationAssembly.GetReferencedAssemblies();

        referencedNames
            .Should().NotContain(
                a => (a.Name ?? string.Empty).StartsWith("Google.Apis", StringComparison.Ordinal),
                because: "Humans.Application must stay free of Google SDK references; Google API calls live behind IWorkspaceUserDirectoryClient in Humans.Infrastructure");
    }

    [Fact]
    public void GoogleWorkspaceUserService_DoesNotReferenceGoogleSdkTypes()
    {
        // Paranoid double-check: the service's module should have no Google.Apis.*
        // types in its metadata references. Catches cases where a stray `using`
        // survives a mass-edit even if csproj doesn't add the package reference.
        var module = typeof(GoogleWorkspaceUserService).Module;
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
                because: "the Application-layer service must not see any Google SDK types — they belong behind IWorkspaceUserDirectoryClient");
    }

    // ── IWorkspaceUserDirectoryClient ────────────────────────────────────────

    [Fact]
    public void IWorkspaceUserDirectoryClient_LivesInApplicationInterfacesNamespace()
    {
        typeof(IWorkspaceUserDirectoryClient).Namespace
            .Should().Be("Humans.Application.Interfaces.GoogleIntegration",
                because: "connector interfaces live alongside other application interfaces per design-rules §2b");
    }

    [Fact]
    public void IWorkspaceUserDirectoryClient_HasNoGoogleSdkTypesInSignatures()
    {
        // Every method parameter and return type must come from Humans.Application
        // or the BCL — never Google.Apis.*. Enforces the "shape-neutral" contract.
        var methods = typeof(IWorkspaceUserDirectoryClient).GetMethods();

        foreach (var method in methods)
        {
            var types = new[] { method.ReturnType }
                .Concat(method.GetParameters().Select(p => p.ParameterType))
                .SelectMany(UnwrapGenericArgs);

            foreach (var t in types)
            {
                (t.Namespace ?? string.Empty)
                    .Should().NotStartWith("Google.Apis",
                        because: $"{method.Name} leaks a Google SDK type through its signature; connector contracts must be shape-neutral");
            }
        }

        static IEnumerable<Type> UnwrapGenericArgs(Type t)
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
}
