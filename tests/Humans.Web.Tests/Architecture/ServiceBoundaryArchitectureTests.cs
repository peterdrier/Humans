using Microsoft.Extensions.DependencyModel;
using System.Reflection;
using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Repositories;
using Humans.Web.Tests.Architecture.Ratchet;

namespace Humans.Web.Tests.Architecture;

public class ServiceBoundaryArchitectureTests
{
    private const string EntityReadReturnBaselinePath =
        "tests/Humans.Web.Tests/Architecture/Baselines/ApplicationServiceEntityReadReturns.baseline.txt";

    /// <summary>
    /// Boundary interfaces on <c>Humans.Users.Contracts</c> that cannot carry their marker: Base references that leaf,
    /// so it cannot reference the markers back without a cycle. The csproj carries the record.
    /// </summary>
    /// <remarks>
    /// Listed by name, not by assembly, so a new unmarked I*Service on the leaf still fails. Delete the list when the
    /// leaf can name the markers again, and re-add the inheritance in the same commit.
    /// </remarks>
    private static readonly HashSet<string> MarkersDroppedForBaseCycle =
    [
        "Humans.Users.Contracts.IAccountDeletionService",
        "Humans.Users.Contracts.IAccountProvisioningService",
        "Humans.Users.Contracts.ICommunicationPreferenceService",
        "Humans.Users.Contracts.IContactFieldService",
        "Humans.Users.Contracts.IHumanLifecycleService",
        "Humans.Users.Contracts.IProfileEditorService",
        "Humans.Users.Contracts.IUserEmailService",
        "Humans.Users.Contracts.IUserService",
    ];

    [HumansFact]
    public void Application_boundary_interfaces_are_marked_as_application_services()
    {
        var unmarked = ApplicationInterfaceTypes()
            .Where(IsApplicationServiceBoundaryName)
            .Where(t => t != typeof(IApplicationService) && t != typeof(IOrchestrator))
            .Where(t => !typeof(IApplicationService).IsAssignableFrom(t))
            .Where(t => !typeof(IOrchestrator).IsAssignableFrom(t))
            .Where(t => !MarkersDroppedForBaseCycle.Contains(t.FullName ?? string.Empty))
            .Select(t => t.FullName)
            .Order(StringComparer.Ordinal)
            .ToList();

        unmarked.Should().BeEmpty(
            because: "I*Service, I*Query, and I*Calculator interfaces are application service boundaries and must be searchable/reforge-addressable via IApplicationService or IOrchestrator");
    }

    [HumansFact]
    public void Repository_named_interfaces_are_marked_as_repositories()
    {
        var unmarked = ApplicationInterfaceTypes()
            .Where(t => t.Name.EndsWith("Repository", StringComparison.Ordinal))
            .Where(t => t != typeof(IRepository))
            .Where(t => !typeof(IRepository).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .Order(StringComparer.Ordinal)
            .ToList();

        unmarked.Should().BeEmpty(
            because: "I*Repository interfaces are persistence boundaries and must be searchable/reforge-addressable via IRepository");
    }

    [HumansFact]
    public void Application_service_read_methods_do_not_add_new_entity_return_types()
    {
        RatchetTestRunner.Run(
            "ApplicationServiceEntityReadReturns",
            EntityReadReturnBaselinePath,
            ScanApplicationServiceEntityReadReturns());
    }

    /// <summary>
    /// Entities that live on a contracts leaf rather than under a <c>*.Domain</c> namespace, so the namespace sweep
    /// misses them. Without this list their baseline rows would read as fixed on unchanged code.
    /// </summary>
    private static readonly Type[] LeafResidentEntities =
    [
        typeof(Users.Contracts.User),
        typeof(Users.Contracts.UserEmail),
        typeof(Users.Contracts.EventParticipation),
        typeof(GoogleIntegration.Contracts.GoogleResource),
        typeof(GoogleIntegration.Contracts.GoogleSyncOutboxEvent),
        typeof(GoogleIntegration.Contracts.SyncServiceSettings),
    ];

    internal static IEnumerable<string> ScanApplicationServiceEntityReadReturns()
    {
        // The Humans.Domain.Entities clause matches nothing today; it re-arms the sweep if an entity lands there.
        var entityTypes = typeof(Base.Enums.GoogleSyncSource).Assembly
            .GetTypes()
            .Where(t => string.Equals(t.Namespace, "Humans.Domain.Entities", StringComparison.Ordinal))
            .Concat(SectionAssemblies()
                .SelectMany(a => a.GetTypes())
                .Where(t => t.Namespace?.EndsWith(".Domain", StringComparison.Ordinal) == true))
            .Concat(LeafResidentEntities)
            .ToHashSet();

        foreach (var serviceType in ApplicationInterfaceTypes()
                     // Both role markers: the axis is exclusive, so a service reclassified as an orchestrator
                     // would otherwise drop out.
                     .Where(t => typeof(IApplicationService).IsAssignableFrom(t)
                                 || typeof(IOrchestrator).IsAssignableFrom(t))
                     .Where(t => t != typeof(IApplicationService) && t != typeof(IOrchestrator))
                     .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            foreach (var (memberName, returnType) in EntityReturnReadMembers(serviceType))
            {
                foreach (var exposedEntity in ExposedTypes(returnType)
                             .Where(entityTypes.Contains)
                             .Distinct()
                             .OrderBy(Display, StringComparer.Ordinal))
                {
                    yield return $"{Display(serviceType)}.{memberName}:{Display(exposedEntity)}";
                }
            }
        }
    }

    // Contracts leaves are scanned too: they carry neither the section namespace nor [Section], and a service
    // interface living entirely on a leaf would otherwise leave the sweep and its baseline rows read as fixed.
    // Base's own interfaces are not scanned; Base declares no entity to expose.
    private static IEnumerable<Type> ApplicationInterfaceTypes()
    {
        return SectionAssemblies()
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsInterface)
            .Concat(SectionContractsAssemblies()
                .SelectMany(a => a.GetTypes())
                .Where(t => t.IsInterface));
    }

    private static IEnumerable<Assembly> SectionAssemblies() =>
        Extensions.SectionDiscoveryExtensions.SectionAssemblies();

    /// <summary>Found by name: a leaf carries no <c>[Section]</c> marker because it registers nothing.</summary>
    private static IEnumerable<Assembly> SectionContractsAssemblies() =>
        DependencyContext.Default?.RuntimeLibraries
            .Where(l => l.Name.StartsWith("Humans.", StringComparison.Ordinal)
                        && l.Name.EndsWith(".Contracts", StringComparison.Ordinal))
            .Select(l =>
            {
                try
                {
                    return Assembly.Load(new AssemblyName(l.Name));
                }
                catch (Exception ex) when (ex is FileNotFoundException or BadImageFormatException)
                {
                    return null;
                }
            })
            .OfType<Assembly>()
        ?? [];

    private static IEnumerable<(string MemberName, Type ReturnType)> EntityReturnReadMembers(Type serviceType)
    {
        // GetMethods() on an interface returns only declared members, so inherited leaf reads must be walked
        // explicitly or they drop out of the ratchet. Distinct by (name, return type) for re-declared members.
        var declaringTypes = new[] { serviceType }.Concat(serviceType.GetInterfaces());

        foreach (var member in declaringTypes
                     .SelectMany(t => t.GetMethods().Where(IsReadMethod)
                         .Select(m => (MemberName: m.Name, ReturnType: m.ReturnType))
                         .Concat(t.GetProperties().Where(p => p.GetMethod is not null)
                             .Select(p => (MemberName: p.Name, ReturnType: p.PropertyType))))
                     .Distinct())
        {
            yield return member;
        }
    }

    // Get*/Find* also match GetOrCreate*/FindOrCreate* upserts. A newly flagged one: return a result record
    // (preferred) or add it to the baseline.
    private static bool IsReadMethod(MethodInfo method) =>
        method.Name.StartsWith("Get", StringComparison.Ordinal) ||
        method.Name.StartsWith("List", StringComparison.Ordinal) ||
        method.Name.StartsWith("Search", StringComparison.Ordinal) ||
        method.Name.StartsWith("Find", StringComparison.Ordinal) ||
        method.Name.StartsWith("Load", StringComparison.Ordinal) ||
        method.Name.StartsWith("Resolve", StringComparison.Ordinal) ||
        method.Name.StartsWith("Fetch", StringComparison.Ordinal) ||
        method.Name.StartsWith("Query", StringComparison.Ordinal) ||
        method.Name.StartsWith("Retrieve", StringComparison.Ordinal) ||
        method.Name.StartsWith("Lookup", StringComparison.Ordinal);

    private static bool IsApplicationServiceBoundaryName(Type type) =>
        type.Name.EndsWith("Service", StringComparison.Ordinal) ||
        type.Name.EndsWith("Query", StringComparison.Ordinal) ||
        type.Name.EndsWith("Calculator", StringComparison.Ordinal);

    private static IEnumerable<Type> ExposedTypes(Type type) =>
        ExposedTypes(type, []);

    private static IEnumerable<Type> ExposedTypes(Type type, HashSet<Type> visited)
    {
        if (type.IsGenericType && (
                type.GetGenericTypeDefinition() == typeof(Task<>) ||
                type.GetGenericTypeDefinition() == typeof(ValueTask<>)))
        {
            foreach (var exposed in ExposedTypes(type.GetGenericArguments()[0], visited))
                yield return exposed;
            yield break;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            foreach (var exposed in ExposedTypes(type.GetGenericArguments()[0], visited))
                yield return exposed;
            yield break;
        }

        if (type.IsArray)
        {
            foreach (var exposed in ExposedTypes(type.GetElementType()!, visited))
                yield return exposed;
            yield break;
        }

        yield return type;

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                foreach (var exposed in ExposedTypes(argument, visited))
                    yield return exposed;
            }
        }

        if (!IsApplicationReturnShape(type) || !visited.Add(type))
            yield break;

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.GetIndexParameters().Length == 0))
        {
            foreach (var exposed in ExposedTypes(property.PropertyType, visited))
                yield return exposed;
        }

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            foreach (var exposed in ExposedTypes(field.FieldType, visited))
                yield return exposed;
        }
    }

    /// <summary>
    /// Types the scan recurses into: anything on a contracts leaf, keyed on the <c>.Contracts</c> namespace so a leaf
    /// folded back into its section stays in scan. Not whole section assemblies: a section-internal DTO wrapping its
    /// own entity crosses no boundary.
    /// </summary>
    private static bool IsApplicationReturnShape(Type type) =>
        type is { IsPrimitive: false, IsEnum: false } &&
        type != typeof(string) &&
        (type.Namespace?.StartsWith("Humans.Application.", StringComparison.Ordinal) == true ||
         type.Namespace?.EndsWith(".Contracts", StringComparison.Ordinal) == true ||
         type.Namespace?.Contains(".Contracts.", StringComparison.Ordinal) == true ||
         type.Assembly.GetName().Name?.EndsWith(".Contracts", StringComparison.Ordinal) == true);

    private static string Display(Type type) =>
        type.FullName?.Replace('+', '.') ?? type.Name;
}
