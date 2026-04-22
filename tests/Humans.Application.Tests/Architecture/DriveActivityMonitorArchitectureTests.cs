using System.Reflection;
using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;
using DriveActivityMonitorService = Humans.Application.Services.GoogleIntegration.DriveActivityMonitorService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 pattern for the Google Integration
/// section's <see cref="DriveActivityMonitorService"/> — migrated under issue
/// #554 (split-off from the umbrella migration). The service now lives in
/// <c>Humans.Application.Services.GoogleIntegration</c> and routes all Google
/// SDK calls through <see cref="IGoogleDriveActivityClient"/>. These tests
/// are the compile-time guarantee that the connector boundary does not leak
/// back into the Application project.
/// </summary>
public class DriveActivityMonitorArchitectureTests
{
    // ── DriveActivityMonitorService ──────────────────────────────────────────

    [Fact]
    public void DriveActivityMonitorService_LivesInHumansApplicationServicesGoogleIntegrationNamespace()
    {
        typeof(DriveActivityMonitorService).Namespace
            .Should().Be("Humans.Application.Services.GoogleIntegration",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [Fact]
    public void DriveActivityMonitorService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(DriveActivityMonitorService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use IDriveActivityMonitorRepository instead (design-rules §3)");
    }

    [Fact]
    public void DriveActivityMonitorService_HasNoDbContextFactoryConstructorParameter()
    {
        var ctor = typeof(DriveActivityMonitorService).GetConstructors().Single();
        var factoryParam = ctor.GetParameters()
            .FirstOrDefault(p =>
                p.ParameterType.IsGenericType &&
                p.ParameterType.GetGenericTypeDefinition() == typeof(IDbContextFactory<>));

        factoryParam.Should().BeNull(
            because: "IDbContextFactory belongs behind the repository boundary, not in an Application-layer service");
    }

    [Fact]
    public void DriveActivityMonitorService_TakesConnectorAndRepository()
    {
        var ctor = typeof(DriveActivityMonitorService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IGoogleDriveActivityClient),
            because: "Google Drive Activity API / Directory API calls go through the shape-neutral connector");
        paramTypes.Should().Contain(typeof(IDriveActivityMonitorRepository),
            because: "SystemSettings and audit-log writes go through the owned repository (design-rules §3)");
        paramTypes.Should().Contain(typeof(ITeamResourceService),
            because: "the list of monitored resources comes from the team-resource section service, not a cross-section DB read");
    }

    [Fact]
    public void DriveActivityMonitorService_IsSealed()
    {
        typeof(DriveActivityMonitorService).IsSealed.Should().BeTrue(
            because: "§15-migrated services are sealed to prevent ad-hoc extension");
    }

    // ── Application assembly cleanliness ─────────────────────────────────────

    [Fact]
    public void HumansApplicationAssembly_HasNoGoogleApisReferences()
    {
        // The Application project must never transitively pull Google.Apis.*.
        // Scanning referenced assemblies is the compile-time guarantee for this.
        var applicationAssembly = typeof(IDriveActivityMonitorService).Assembly;
        var referencedNames = applicationAssembly.GetReferencedAssemblies();

        referencedNames
            .Should().NotContain(
                a => (a.Name ?? string.Empty).StartsWith("Google.Apis", StringComparison.Ordinal),
                because: "Humans.Application must stay free of Google SDK references; Google API calls live behind IGoogleDriveActivityClient in Humans.Infrastructure");
    }

    [Fact]
    public void DriveActivityMonitorService_DoesNotReferenceGoogleSdkTypes()
    {
        // Paranoid double-check: the service's module should have no Google.Apis.*
        // types in its metadata references. Catches cases where a stray `using`
        // survives a mass-edit even if csproj doesn't add the package reference.
        var module = typeof(DriveActivityMonitorService).Module;
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
                because: "the Application-layer service must not see any Google SDK types — they belong behind IGoogleDriveActivityClient");
    }

    // ── IGoogleDriveActivityClient ───────────────────────────────────────────

    [Fact]
    public void IGoogleDriveActivityClient_LivesInApplicationInterfacesNamespace()
    {
        typeof(IGoogleDriveActivityClient).Namespace
            .Should().Be("Humans.Application.Interfaces",
                because: "connector interfaces live alongside other application interfaces per design-rules §2b");
    }

    [Fact]
    public void IGoogleDriveActivityClient_HasNoGoogleSdkTypesInSignatures()
    {
        // Every method parameter and return type must come from Humans.Application
        // or the BCL — never Google.Apis.*. Enforces the "shape-neutral" contract.
        var methods = typeof(IGoogleDriveActivityClient).GetMethods();

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

    // ── IDriveActivityMonitorRepository ──────────────────────────────────────

    [Fact]
    public void IDriveActivityMonitorRepository_LivesInApplicationInterfacesRepositoriesNamespace()
    {
        typeof(IDriveActivityMonitorRepository).Namespace
            .Should().Be("Humans.Application.Interfaces.Repositories",
                because: "repository interfaces live in Humans.Application.Interfaces.Repositories per design-rules §3");
    }

    [Fact]
    public void DriveActivityMonitorRepository_IsSealed()
    {
        var repoType = typeof(Humans.Infrastructure.Repositories.DriveActivityMonitorRepository);
        repoType.IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }
}
