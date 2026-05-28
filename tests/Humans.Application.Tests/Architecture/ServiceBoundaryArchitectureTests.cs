using System.Reflection;
using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Tests.Architecture.Ratchet;

namespace Humans.Application.Tests.Architecture;

public class ServiceBoundaryArchitectureTests
{
    private const string EntityReadReturnBaselinePath =
        "tests/Humans.Application.Tests/Architecture/Baselines/ApplicationServiceEntityReadReturns.baseline.txt";

    private static readonly IReadOnlyDictionary<Type, string> RepositoryOwners =
        new Dictionary<Type, string>
        {
            [typeof(IAccountMergeRepository)] = "Humans",
            [typeof(IAdminDatabaseDiagnosticsRepository)] = "Admin",
            [typeof(IAgentRepository)] = "Agent",
            [typeof(IApplicationRepository)] = "Governance",
            [typeof(IAuditLogRepository)] = "AuditLog",
            [typeof(IBudgetRepository)] = "Budget",
            [typeof(ICalendarRepository)] = "Calendar",
            [typeof(ICampaignRepository)] = "Campaigns",
            [typeof(ICampRepository)] = "Camps",
            [typeof(ICityPlanningRepository)] = "CityPlanning",
            [typeof(ICommunicationPreferenceRepository)] = "Humans",
            [typeof(IConsentRepository)] = "Consent",
            [typeof(IContainerRepository)] = "Containers",
            [typeof(IDriveActivityMonitorRepository)] = "GoogleIntegration",
            [typeof(IEmailOutboxRepository)] = "Email",
            [typeof(IEventRepository)] = "Events",
            [typeof(IExpenseRepository)] = "Expenses",
            [typeof(IFeedbackRepository)] = "Feedback",
            [typeof(IGoogleResourceRepository)] = "GoogleIntegration",
            [typeof(IGoogleSyncOutboxRepository)] = "GoogleIntegration",
            [typeof(IHoldedRepository)] = "Finance",
            [typeof(IIssuesRepository)] = "Issues",
            [typeof(ILegalDocumentRepository)] = "Legal",
            [typeof(INotificationRepository)] = "Notifications",
            [typeof(IRoleAssignmentRepository)] = "Auth",
            [typeof(IShiftManagementRepository)] = "Shifts",
            [typeof(IShiftSignupRepository)] = "Shifts",
            [typeof(IStoreRepository)] = "Store",
            [typeof(ISyncSettingsRepository)] = "GoogleIntegration",
            [typeof(ITeamRepository)] = "Teams",
            [typeof(ITicketRepository)] = "Tickets",
            [typeof(ITicketTransferRepository)] = "Tickets",
            [typeof(IUserRepository)] = "Humans",
            [typeof(IVolunteerTrackingRepository)] = "Shifts",
        };

    [HumansFact]
    public void Application_boundary_interfaces_are_marked_as_application_services()
    {
        var unmarked = ApplicationInterfaceTypes()
            .Where(IsApplicationServiceBoundaryName)
            .Where(t => t != typeof(IApplicationService) && t != typeof(IOrchestrator))
            .Where(t => !typeof(IApplicationService).IsAssignableFrom(t))
            .Where(t => !typeof(IOrchestrator).IsAssignableFrom(t))
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
    public void Repository_ownership_map_covers_all_repositories()
    {
        var missingOwnership = RepositoryInterfaceTypes()
            .Where(t => t != typeof(IRepository))
            .Where(t => !RepositoryOwners.ContainsKey(t))
            .Select(Display)
            .Order(StringComparer.Ordinal)
            .ToList();

        missingOwnership.Should().BeEmpty(
            because: "cross-section repository injection checks must use exact repository ownership, not name prefixes");
    }

    [HumansFact]
    public void Users_and_profiles_share_one_repository_ownership_section()
    {
        RepositoryOwners[typeof(IUserRepository)].Should().Be("Humans");
        ServiceSection(typeof(Humans.Application.Services.Users.UserService)).Should().Be("Humans");
        ServiceSection(typeof(Humans.Application.Services.Profiles.ProfileService)).Should().Be("Humans");
    }

    [HumansFact]
    public void Application_service_read_methods_do_not_add_new_entity_return_types()
    {
        RatchetTestRunner.Run(
            "ApplicationServiceEntityReadReturns",
            EntityReadReturnBaselinePath,
            ScanApplicationServiceEntityReadReturns());
    }

    internal static IEnumerable<string> ScanApplicationServiceEntityReadReturns()
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

    private static IEnumerable<Type> ApplicationInterfaceTypes() =>
        typeof(IApplicationService).Assembly.GetTypes()
            .Where(t => t.IsInterface)
            .Where(t => t.Namespace?.StartsWith("Humans.Application.Interfaces", StringComparison.Ordinal) == true);

    private static IEnumerable<Type> RepositoryInterfaceTypes() =>
        ApplicationInterfaceTypes()
            .Where(t => typeof(IRepository).IsAssignableFrom(t));

    private static string ServiceSection(Type serviceType)
    {
        var section = serviceType.Namespace!.Split('.')[3];
        return section is "Users" or "Profile" or "Profiles" ? "Humans" : section;
    }

    private static IEnumerable<(string MemberName, Type ReturnType)> EntityReturnReadMembers(Type serviceType)
    {
        foreach (var method in serviceType.GetMethods().Where(IsReadMethod))
            yield return (method.Name, method.ReturnType);

        foreach (var property in serviceType.GetProperties().Where(p => p.GetMethod is not null))
            yield return (property.Name, property.PropertyType);
    }

    // Note: Get*/Find* also match GetOrCreate*/FindOrCreate* upsert mutations.
    // Per service-entity-boundary-ratchet.md, mutations that temporarily return entities
    // are allowed as ratcheted debt. If a new GetOrCreate* method is flagged here,
    // either use a result record (preferred) or add it to the baseline with a comment.
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

    private static bool IsApplicationReturnShape(Type type) =>
        type is { IsPrimitive: false, IsEnum: false } &&
        type != typeof(string) &&
        type.Namespace?.StartsWith("Humans.Application.", StringComparison.Ordinal) == true;

    private static string Display(Type type) =>
        type.FullName?.Replace('+', '.') ?? type.Name;
}
