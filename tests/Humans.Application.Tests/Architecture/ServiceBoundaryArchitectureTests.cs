using System.Reflection;
using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Tests.Architecture.Ratchet;
using Humans.Web.Controllers;
using Xunit;

namespace Humans.Application.Tests.Architecture;

public class ServiceBoundaryArchitectureTests
{
    private const string EntityReadReturnBaselinePath =
        "tests/Humans.Application.Tests/Architecture/Baselines/ApplicationServiceEntityReadReturns.baseline.txt";

    private const string CrossSectionRepositoryInjectionBaselinePath =
        "tests/Humans.Application.Tests/Architecture/Baselines/CrossSectionRepositoryInjection.baseline.txt";

    private static readonly IReadOnlyDictionary<string, string[]> RepositoryOwnershipPrefixes =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Agent"] = ["Agent"],
            ["AuditLog"] = ["AuditLog"],
            ["Auth"] = ["RoleAssignment"],
            ["Budget"] = ["Budget"],
            ["Calendar"] = ["Calendar"],
            ["Campaigns"] = ["Campaign"],
            ["Camps"] = ["Camp", "CampRole"],
            ["CityPlanning"] = ["CityPlanning"],
            ["Consent"] = ["Consent"],
            ["Email"] = ["EmailOutbox"],
            ["Feedback"] = ["Feedback"],
            ["GoogleIntegration"] = ["DriveActivityMonitor", "Google", "SyncSettings"],
            ["Governance"] = ["Application"],
            ["Issues"] = ["Issues"],
            ["Legal"] = ["LegalDocument"],
            ["Notifications"] = ["Notification"],
            ["Profile"] = ["AccountMerge", "CommunicationPreference", "ContactField", "Profile", "UserEmail"],
            ["Shifts"] = ["GeneralAvailability", "Shift"],
            ["Store"] = ["Store"],
            ["Teams"] = ["Team"],
            ["Tickets"] = ["Ticket", "TicketingBudget"],
            ["Users"] = ["User", "UserEmail"],
        };

    [HumansFact]
    public void Service_named_interfaces_are_marked_as_application_services()
    {
        var unmarked = ApplicationInterfaceTypes()
            .Where(t => t.Name.EndsWith("Service", StringComparison.Ordinal))
            .Where(t => t != typeof(IApplicationService))
            .Where(t => !typeof(IApplicationService).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .Order(StringComparer.Ordinal)
            .ToList();

        unmarked.Should().BeEmpty(
            because: "I*Service interfaces are application service boundaries and must be searchable/reforge-addressable via IApplicationService");
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

    [HumansFact]
    public void Web_classes_do_not_inject_repositories()
    {
        var violations = typeof(AccountController).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .SelectMany(type => ConstructorParameters(type)
                .Where(p => typeof(IRepository).IsAssignableFrom(p.ParameterType))
                .Select(p => $"{Display(type)}:{p.ParameterType.Name}"));

        violations.Should().BeEmpty(
            because: "Web depends on application services, not persistence repositories");
    }

    [HumansFact]
    public void Application_services_do_not_add_cross_section_repository_injections()
    {
        RatchetTestRunner.Run(
            "CrossSectionRepositoryInjection",
            CrossSectionRepositoryInjectionBaselinePath,
            ScanCrossSectionRepositoryInjections());
    }

    private static IEnumerable<string> ScanApplicationServiceEntityReadReturns()
    {
        var entityTypes = typeof(Humans.Domain.Entities.Team).Assembly
            .GetTypes()
            .Where(t => string.Equals(t.Namespace, "Humans.Domain.Entities", StringComparison.Ordinal))
            .ToHashSet();

        foreach (var serviceType in ApplicationInterfaceTypes()
                     .Where(t => typeof(IApplicationService).IsAssignableFrom(t))
                     .Where(t => t != typeof(IApplicationService))
                     .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            foreach (var method in serviceType.GetMethods().Where(IsReadMethod))
            {
                var exposedEntity = ExposedTypes(method.ReturnType).FirstOrDefault(entityTypes.Contains);
                if (exposedEntity is null) continue;

                yield return $"{Display(serviceType)}.{method.Name}:{Display(exposedEntity)}";
            }
        }
    }

    private static IEnumerable<string> ScanCrossSectionRepositoryInjections()
    {
        foreach (var serviceType in typeof(IApplicationService).Assembly.GetTypes()
                     .Where(t => t.IsClass && !t.IsAbstract)
                     .Where(t => t.Namespace?.StartsWith("Humans.Application.Services.", StringComparison.Ordinal) == true)
                     .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            var section = serviceType.Namespace!.Split('.')[3];
            var allowedPrefixes = RepositoryOwnershipPrefixes.GetValueOrDefault(section, []);

            foreach (var parameter in ConstructorParameters(serviceType)
                         .Where(p => typeof(IRepository).IsAssignableFrom(p.ParameterType))
                         .OrderBy(p => p.ParameterType.Name, StringComparer.Ordinal))
            {
                var repositoryName = parameter.ParameterType.Name;
                var owned = allowedPrefixes.Any(prefix =>
                    repositoryName.StartsWith("I" + prefix, StringComparison.Ordinal));
                if (owned) continue;

                yield return $"{Display(serviceType)}:{repositoryName}";
            }
        }
    }

    private static IEnumerable<Type> ApplicationInterfaceTypes() =>
        typeof(IApplicationService).Assembly.GetTypes()
            .Where(t => t.IsInterface)
            .Where(t => t.Namespace?.StartsWith("Humans.Application.Interfaces", StringComparison.Ordinal) == true);

    private static IEnumerable<ParameterInfo> ConstructorParameters(Type type) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(c => c.GetParameters());

    private static bool IsReadMethod(MethodInfo method) =>
        method.Name.StartsWith("Get", StringComparison.Ordinal) ||
        method.Name.StartsWith("List", StringComparison.Ordinal) ||
        method.Name.StartsWith("Search", StringComparison.Ordinal) ||
        method.Name.StartsWith("Find", StringComparison.Ordinal) ||
        method.Name.StartsWith("Load", StringComparison.Ordinal) ||
        method.Name.StartsWith("Resolve", StringComparison.Ordinal);

    private static IEnumerable<Type> ExposedTypes(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            foreach (var exposed in ExposedTypes(type.GetGenericArguments()[0]))
                yield return exposed;
            yield break;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            foreach (var exposed in ExposedTypes(type.GetGenericArguments()[0]))
                yield return exposed;
            yield break;
        }

        if (type.IsArray)
        {
            foreach (var exposed in ExposedTypes(type.GetElementType()!))
                yield return exposed;
            yield break;
        }

        yield return type;

        if (!type.IsGenericType) yield break;

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var exposed in ExposedTypes(argument))
                yield return exposed;
        }
    }

    private static string Display(Type type) =>
        type.FullName?.Replace('+', '.') ?? type.Name;
}
