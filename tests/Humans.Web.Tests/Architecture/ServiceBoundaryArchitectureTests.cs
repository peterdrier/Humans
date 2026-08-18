using Microsoft.Extensions.DependencyModel;
using System.Reflection;
using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Web.Tests.Architecture.Ratchet;
using Humans.Users.Data.Repositories;

namespace Humans.Web.Tests.Architecture;

public class ServiceBoundaryArchitectureTests
{
    private const string EntityReadReturnBaselinePath =
        "tests/Humans.Web.Tests/Architecture/Baselines/ApplicationServiceEntityReadReturns.baseline.txt";

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

    /// <summary>
    /// Same problem one tier up: the platform diagnostics repository is <c>internal</c> to
    /// <c>Humans.Web</c> since G5 lane 5b-6 deleted <c>Humans.Infrastructure</c>.
    /// </summary>
    private static Type HostRepository(string fullName) =>
        typeof(Web.Extensions.InfrastructureServiceCollectionExtensions).Assembly
            .GetType(fullName, throwOnError: false)
        ?? throw new InvalidOperationException(
            $"{fullName} not found in Humans.Web — did it move or get renamed?");

    private static readonly IReadOnlyDictionary<Type, string> RepositoryOwners =
        new Dictionary<Type, string>
        {
            [SectionRepository("Humans.Events.Data.IEventRepository")] = "Events",
            [SectionRepository("Humans.SystemSettings.Data.ISystemSettingsRepository")] = "SystemSettings",
            [SectionRepository("Humans.Store.Data.IStoreRepository")] = "Store",
            [typeof(IAccountMergeRepository)] = "Humans",
            [HostRepository("Humans.Infrastructure.Repositories.Admin.IAdminDatabaseDiagnosticsRepository")] = "Admin",
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
            [SectionRepository("Humans.GoogleIntegration.Data.IGoogleResourceRepository")] = "GoogleIntegration",
            [SectionRepository("Humans.GoogleIntegration.Data.IGoogleSyncOutboxRepository")] = "GoogleIntegration",
            [SectionRepository("Humans.Finance.Data.IHoldedRepository")] = "Finance",
            [SectionRepository("Humans.Holded.Data.IHoldedMirrorRepository")] = "Holded",
            [SectionRepository("Humans.Issues.Data.IIssuesRepository")] = "Issues",
            [SectionRepository("Humans.Consent.Data.ILegalDocumentRepository")] = "Legal",
            [SectionRepository("Humans.Notifications.Data.INotificationRepository")] = "Notifications",
            [SectionRepository("Humans.Auth.Data.IRoleAssignmentRepository")] = "Auth",
            [SectionRepository("Humans.Shifts.Data.IShiftManagementRepository")] = "Shifts",
            [SectionRepository("Humans.Surveys.Data.ISurveyRepository")] = "Surveys",
            [SectionRepository("Humans.GoogleIntegration.Data.ISyncSettingsRepository")] = "GoogleIntegration",
            [SectionRepository("Humans.Teams.Data.ITeamRepository")] = "Teams",
            [SectionRepository("Humans.Tickets.Data.ITicketRepository")] = "Tickets",
            [SectionRepository("Humans.Tickets.Data.ITicketTransferRepository")] = "Tickets",
            [typeof(IUserRepository)] = "Humans",
            [SectionRepository("Humans.Shifts.Data.IVolunteerTrackingRepository")] = "Shifts",
        };

    /// <summary>
    /// COVERAGE REDUCED (G5 lane 3b, nobodies-collective/Humans#866). These eight interfaces
    /// are application service boundaries by name and would be marked, but cannot be.
    /// </summary>
    /// <remarks>
    /// <c>IApplicationService</c> and <c>IOrchestrator</c> live in <c>Humans.Interfaces</c>
    /// (Base). Base references <c>Humans.Users.Contracts</c> — <c>HumansControllerBase</c>
    /// injects <c>IUserServiceRead</c> and <c>Humans.UI</c> names the <c>User</c> entity — so
    /// that leaf must hold no reference back, or 4b-iii closes an assembly cycle when
    /// <c>HumansControllerBase</c> moves into Base. Peter's ruling: the migration outranks the
    /// marker. The markers were dropped and each declaration carries a COVERAGE REDUCED comment.
    ///
    /// Listed by name rather than excluding the whole assembly ON PURPOSE: a NEW unmarked
    /// I*Service on that leaf still fails this test. The exclusion covers exactly the interfaces
    /// that lost a marker they used to carry, and nothing else. <c>INonCompliantMemberSuspension</c>
    /// also lost <c>IOrchestrator</c> but is absent here because the name predicate below never
    /// matched it.
    ///
    /// <c>IHumanLifecycleService</c> stays listed because this test inspects interfaces, but its
    /// analyzer coverage is back: <c>HumanLifecycleService</c> carries <c>IOrchestrator</c> and
    /// HUM0026/HUM0027 key on the implementing class.
    ///
    /// Exit condition: delete this list when the leaf can name the markers again — either
    /// because Base stops referencing it, or because the markers move somewhere it may
    /// reference. Re-add the inheritance in the same commit.
    /// </remarks>
    private static readonly HashSet<string> MarkersDroppedForBaseCycle =
    [
        "Humans.Users.Contracts.IAccountProvisioningService",
        "Humans.Users.Contracts.ICommunicationPreferenceService",
        "Humans.Users.Contracts.IContactFieldService",
        "Humans.Users.Contracts.IHumanLifecycleService",
        "Humans.Users.Contracts.IProfileEditorService",
        "Humans.Users.Contracts.IUserEmailService",
        "Humans.Users.Contracts.IUserParticipationBackfillService",
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
    public void Application_service_read_methods_do_not_add_new_entity_return_types()
    {
        RatchetTestRunner.Run(
            "ApplicationServiceEntityReadReturns",
            EntityReadReturnBaselinePath,
            ScanApplicationServiceEntityReadReturns());
    }

    /// <summary>
    /// EF entity types that sit on a contracts leaf rather than under a <c>*.Domain</c>
    /// namespace, so neither half of the sweep above finds them.
    /// </summary>
    /// <remarks>
    /// <c>User : IdentityUser&lt;Guid&gt;</c> is named by Base (<c>Humans.Interfaces</c>; the namers
    /// arrived there from <c>Humans.UI</c> in G5 lanes 4b-iii A and B) and by ~48 files
    /// across Shell and twenty test projects, and Base cannot reference a section, so the nine
    /// Users/Profiles entities are public on <c>Humans.Users.Contracts</c>
    /// (nobodies-collective/Humans#866, G5 lane 2). Without this list the two
    /// <c>Humans.Domain.Entities.User</c> rows in the baseline read as *fixed* on byte-identical
    /// code — the silent shrink design §10 warns about, arriving through a third keying (not a
    /// path, not an assembly: the namespace convention that says "entities live in .Domain").
    /// The list empties when the entities are internalised; see the lane 2 handoff.
    /// </remarks>
    private static readonly Type[] LeafResidentEntities =
    [
        typeof(Humans.Users.Contracts.User),
        typeof(Humans.Users.Contracts.UserEmail),
        typeof(Humans.Users.Contracts.EventParticipation),
        typeof(Humans.Users.Contracts.Profile),
        typeof(Humans.Users.Contracts.ContactField),
        typeof(Humans.Users.Contracts.ProfileLanguage),
        typeof(Humans.Users.Contracts.VolunteerHistoryEntry),
        typeof(Humans.Users.Contracts.CommunicationPreference),
        typeof(Humans.Users.Contracts.AccountMergeRequest),
        // GoogleIntegration's three entities, same shape and same reason
        // (nobodies-collective/Humans#866, G5 lane 4b-2j): GoogleResource and
        // GoogleSyncOutboxEvent are public members of IGoogleSyncService /
        // IGoogleSyncOutboxService on Humans.GoogleIntegration.Contracts, and a leaf
        // cannot reference its own section project, so they live on the leaf rather
        // than under Humans.GoogleIntegration.Domain.
        typeof(Humans.GoogleIntegration.Contracts.GoogleResource),
        typeof(Humans.GoogleIntegration.Contracts.GoogleSyncOutboxEvent),
        typeof(Humans.GoogleIntegration.Contracts.SyncServiceSettings),
    ];

    internal static IEnumerable<string> ScanApplicationServiceEntityReadReturns()
    {
        // Shell-era entities plus each G5 section's own Domain/ namespace
        // (nobodies-collective/Humans#866). Without the section half, a section that moves
        // takes its entity-returning reads out of this ratchet's sight and the removal
        // reads as "you fixed it" — the exact silent-shrink §10 warns about.
        // Re-anchored off Humans.Domain.Entities.GoogleResource, which moved to
        // Humans.GoogleIntegration.Contracts in G5 lane 4b-2j and took the last three
        // types in Humans.Domain.Entities with it. The namespace filter below now
        // matches nothing; the clause is kept (anchored on a type still resident in
        // Humans.Domain) so the sweep re-arms if anything lands back there before
        // phase 5a deletes the project. Coverage did not shrink — the three entities
        // are listed in LeafResidentEntities above.
        var entityTypes = typeof(Humans.Domain.Enums.GoogleSyncSource).Assembly
            .GetTypes()
            .Where(t => string.Equals(t.Namespace, "Humans.Domain.Entities", StringComparison.Ordinal))
            .Concat(SectionAssemblies()
                .SelectMany(a => a.GetTypes())
                .Where(t => t.Namespace?.EndsWith(".Domain", StringComparison.Ordinal) == true))
            .Concat(LeafResidentEntities)
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
    // …and the contracts leaves, which carry neither the namespace nor the [Section]
    // attribute the two clauses above key on. A service interface that lives *entirely* on
    // a leaf — with no Base-side interface deriving from it — leaves this sweep the moment
    // it is carved, and its baseline rows read as fixed on byte-identical code. The
    // GetInterfaces() walk in EntityReturnReadMembers only rescues the read-split shape
    // (I<Section>Service : I<Section>ServiceRead, where the deriving half stays behind);
    // Users' IAccountProvisioningService is the whole-interface case and is what surfaced
    // this (nobodies-collective/Humans#866, lane 2 PR A).
    // The Humans.Application half of this sweep is gone (G5 lane 5c): the assembly holds no
    // types, so the anchor it needed no longer exists. Every interface it used to contribute
    // has landed either on a section, on a contracts leaf, or in Humans.Interfaces.
    // COVERAGE NOTE: the marker/infra interfaces that now live in Humans.Interfaces under
    // Humans.Application.Interfaces.* (IFileStorage, IGuideContentSource, IHumansMetrics,
    // the cache invalidators, …) are no longer scanned. They cannot regress this rule —
    // Humans.Interfaces declares no entity type to expose. It stopped being EF-free at G5
    // lane 3a-2, which put the AddSectionDbContext cluster there, but that cluster is
    // registration plumbing over an open TContext: the assembly still maps no table and owns
    // no DbSet, so there is nothing there for an entity-returning read to leak.
    private static IEnumerable<Type> ApplicationInterfaceTypes()
    {
        return SectionAssemblies()
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsInterface)
            .Concat(SectionContractsAssemblies()
                .SelectMany(a => a.GetTypes())
                .Where(t => t.IsInterface));
    }

    /// <summary>
    /// Every G5 section assembly, via the same discovery the runtime uses, so this file
    /// never carries a hard-coded list of moved sections.
    /// </summary>
    private static IEnumerable<Assembly> SectionAssemblies() =>
        Web.Extensions.SectionDiscoveryExtensions.SectionAssemblies();

    /// <summary>
    /// Every G5 contracts leaf. Discovered from the dependency graph by name rather than by
    /// <c>[Section]</c> — a leaf deliberately carries no section marker, because it is not
    /// an MVC application part and registers no services.
    /// </summary>
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

    private static IEnumerable<Type> RepositoryInterfaceTypes() =>
        ApplicationInterfaceTypes()
            .Where(t => typeof(IRepository).IsAssignableFrom(t));

    private static IEnumerable<(string MemberName, Type ReturnType)> EntityReturnReadMembers(Type serviceType)
    {
        // Base interfaces included: Type.GetMethods() on an interface returns only
        // *declared* members, so a service whose entity-returning reads were split onto a
        // section contracts leaf it inherits (Shifts, nobodies-collective/Humans#866) would
        // drop out of this ratchet while the code stands still — the silent shrink §10
        // warns about, and the leaf itself is not scanned because it carries no
        // IApplicationService marker. Distinct by (name, return type): a member
        // re-declared on the deriving interface would otherwise be yielded twice.
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

    /// <summary>
    /// Types this scan recurses *into* looking for an exposed entity: Base DTOs, plus
    /// anything on a G5 section's contracts leaf (nobodies-collective/Humans#866).
    /// </summary>
    /// <remarks>
    /// The leaf half is what this rule is about — a result record that moves onto a leaf
    /// keeps wrapping whatever entity it wrapped, and keying the recursion on the
    /// <c>Humans.Application.</c> namespace alone stops it at the assembly boundary and
    /// reports the violation as fixed while the code stands still. Shifts'
    /// <c>UrgentShift(Shift, …)</c> is the case that surfaced it.
    /// <para>
    /// Deliberately *not* widened to whole section assemblies: a section-internal DTO
    /// wrapping that section's own internal entity crosses no boundary, and recursing into
    /// them adds 96 rows across twenty sections that this rule was never asserting about.
    /// </para>
    /// <para>
    /// Keyed on the <c>.Contracts</c> namespace segment rather than the assembly name so a
    /// leaf folded back into its section project (which keeps the namespace) stays in scan.
    /// </para>
    /// </remarks>
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
