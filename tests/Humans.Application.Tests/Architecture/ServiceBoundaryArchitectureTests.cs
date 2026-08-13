using Humans.Teams.Data;
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

    /// <summary>
    /// A G5 section's repository interface is <c>internal</c> to its own assembly
    /// (nobodies-collective/Humans#866), so it cannot be named with <c>typeof</c> here.
    /// Resolved by reflection instead — the row stays in the map, and the ownership test
    /// keeps covering the section after it moves.
    /// </summary>
    private static Type SectionRepository(string fullName) =>
        SectionAssemblies()
            .Select(a => a.GetType(fullName, throwOnError: false))
            .FirstOrDefault(t => t is not null)
        ?? throw new InvalidOperationException(
            $"{fullName} not found in any section assembly — did the section move or rename it?");

    private static readonly IReadOnlyDictionary<Type, string> RepositoryOwners =
        new Dictionary<Type, string>
        {
            [SectionRepository("Humans.Events.Data.IEventRepository")] = "Events",
            [SectionRepository("Humans.SystemSettings.Data.ISystemSettingsRepository")] = "SystemSettings",
            [SectionRepository("Humans.Store.Data.IStoreRepository")] = "Store",
            [typeof(IAccountMergeRepository)] = "Humans",
            [typeof(IAdminDatabaseDiagnosticsRepository)] = "Admin",
            [SectionRepository("Humans.Agent.Data.IAgentRepository")] = "Agent",
            [SectionRepository("Humans.Governance.Data.IApplicationRepository")] = "Governance",
            [SectionRepository("Humans.AuditLog.Data.IAuditLogRepository")] = "AuditLog",
            [SectionRepository("Humans.Budget.Data.IBudgetRepository")] = "Budget",
            [SectionRepository("Humans.Calendar.Data.ICalendarRepository")] = "Calendar",
            [SectionRepository("Humans.Campaigns.Data.ICampaignRepository")] = "Campaigns",
            [SectionRepository("Humans.Camps.Data.ICampRepository")] = "Camps",
            [SectionRepository("Humans.CityPlanning.Data.ICityPlanningRepository")] = "CityPlanning",
            [typeof(ICommunicationPreferenceRepository)] = "Humans",
            [SectionRepository("Humans.Consent.Data.IConsentRepository")] = "Consent",
            [SectionRepository("Humans.Containers.Data.IContainerRepository")] = "Containers",
            [SectionRepository("Humans.Email.Data.IEmailOutboxRepository")] = "Email",
            [SectionRepository("Humans.Expenses.Data.IExpenseRepository")] = "Expenses",
            [SectionRepository("Humans.Feedback.Data.IFeedbackRepository")] = "Feedback",
            [SectionRepository("Humans.Gate.Data.IGateRepository")] = "Gate",
            [typeof(IGoogleResourceRepository)] = "GoogleIntegration",
            [typeof(IGoogleSyncOutboxRepository)] = "GoogleIntegration",
            [SectionRepository("Humans.Finance.Data.IHoldedRepository")] = "Finance",
            [SectionRepository("Humans.Holded.Data.IHoldedMirrorRepository")] = "Holded",
            [SectionRepository("Humans.Issues.Data.IIssuesRepository")] = "Issues",
            [SectionRepository("Humans.Consent.Data.ILegalDocumentRepository")] = "Legal",
            [SectionRepository("Humans.Notifications.Data.INotificationRepository")] = "Notifications",
            [SectionRepository("Humans.Auth.Data.IRoleAssignmentRepository")] = "Auth",
            [typeof(IShiftManagementRepository)] = "Shifts",
            [SectionRepository("Humans.Surveys.Data.ISurveyRepository")] = "Surveys",
            [typeof(ISyncSettingsRepository)] = "GoogleIntegration",
            [SectionRepository("Humans.Teams.Data.ITeamRepository")] = "Teams",
            [SectionRepository("Humans.Tickets.Data.ITicketRepository")] = "Tickets",
            [SectionRepository("Humans.Tickets.Data.ITicketTransferRepository")] = "Tickets",
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
        // Shell-era entities plus each G5 section's own Domain/ namespace
        // (nobodies-collective/Humans#866). Without the section half, a section that moves
        // takes its entity-returning reads out of this ratchet's sight and the removal
        // reads as "you fixed it" — the exact silent-shrink §10 warns about.
        var entityTypes = typeof(Humans.Domain.Entities.User).Assembly
            .GetTypes()
            .Where(t => string.Equals(t.Namespace, "Humans.Domain.Entities", StringComparison.Ordinal))
            .Concat(SectionAssemblies()
                .SelectMany(a => a.GetTypes())
                .Where(t => t.Namespace?.EndsWith(".Domain", StringComparison.Ordinal) == true))
            .ToHashSet();

        foreach (var serviceType in ApplicationInterfaceTypes()
                     // Both role markers — the axis is exclusive, so scanning only
                     // IApplicationService would drop a service the moment it is
                     // reclassified as an orchestrator.
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

    // Anchored to a type that lives in Humans.Application — the marker
    // interfaces (IApplicationService, IRepository, …) moved to the
    // Humans.Interfaces assembly, keeping their namespaces. Section projects
    // (nobodies-collective/Humans#866) are scanned too: their service and repository
    // interfaces are internal and live under Humans.<Section>.*, so a namespace filter
    // anchored on Humans.Application.Interfaces alone would stop seeing a section the
    // moment it moves, quietly shrinking every ratchet built on this.
    private static IEnumerable<Type> ApplicationInterfaceTypes() =>
        typeof(IUserRepository).Assembly.GetTypes()
            .Where(t => t.IsInterface)
            .Where(t => t.Namespace?.StartsWith("Humans.Application.Interfaces", StringComparison.Ordinal) == true)
            .Concat(SectionAssemblies()
                .SelectMany(a => a.GetTypes())
                .Where(t => t.IsInterface));

    /// <summary>
    /// Every G5 section assembly, via the same discovery the runtime uses, so this file
    /// never carries a hard-coded list of moved sections.
    /// </summary>
    private static IEnumerable<Assembly> SectionAssemblies() =>
        Web.Extensions.SectionDiscoveryExtensions.SectionAssemblies();

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
