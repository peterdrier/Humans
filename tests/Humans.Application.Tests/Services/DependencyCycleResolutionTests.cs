using System.Reflection;
using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Auth;
using Humans.Application.Services.Email;
using Humans.Application.Services.Profiles;
using Humans.Application.Services.Shifts;
using Humans.Application.Services.Teams;
using Humans.Application.Services.Users;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Application.Tests.Services;

public sealed class DependencyCycleResolutionTests : ServiceTestHarness
{
    [HumansFact]
    public void IUserService_Resolves_WhenTeamServiceAndRoleAssignmentServiceAreRegistered()
    {
        var services = new ServiceCollection();

        services.AddScoped(_ => new HumansDbContext(DbOptions));
        services.AddSingleton<IMemoryCache>(_ => new MemoryCache(new MemoryCacheOptions()));

        services.AddScoped<IUserRepository>(_ => Substitute.For<IUserRepository>());
        services.AddScoped<IUserEmailRepository>(_ => Substitute.For<IUserEmailRepository>());
        services.AddScoped<IProfileRepository>(_ => Substitute.For<IProfileRepository>());
        services.AddScoped<IContactFieldRepository>(_ => Substitute.For<IContactFieldRepository>());
        services.AddScoped<ICommunicationPreferenceRepository>(_ => Substitute.For<ICommunicationPreferenceRepository>());
        services.AddScoped<IUserInfoInvalidator>(_ => Substitute.For<IUserInfoInvalidator>());
        services.AddScoped<IRoleAssignmentRepository>(_ => Substitute.For<IRoleAssignmentRepository>());
        services.AddScoped<IShiftManagementRepository>(_ => Substitute.For<IShiftManagementRepository>());
        services.AddScoped<IAuditLogService>(_ => Substitute.For<IAuditLogService>());
        services.AddScoped<IEmailService>(_ => Substitute.For<IEmailService>());
        services.AddScoped<INotificationEmitter>(_ => Substitute.For<INotificationEmitter>());
        services.AddScoped<ISystemTeamSync>(_ => Substitute.For<ISystemTeamSync>());
        services.AddScoped<INavBadgeCacheInvalidator>(_ => Substitute.For<INavBadgeCacheInvalidator>());
        services.AddScoped<IRoleAssignmentClaimsCacheInvalidator>(_ => Substitute.For<IRoleAssignmentClaimsCacheInvalidator>());
        services.AddScoped<ITeamRepository>(_ => Substitute.For<ITeamRepository>());
        services.AddScoped<INotificationMeterCacheInvalidator>(_ => Substitute.For<INotificationMeterCacheInvalidator>());
        services.AddScoped<IShiftAuthorizationInvalidator>(_ => Substitute.For<IShiftAuthorizationInvalidator>());
        services.AddScoped<IAdminAuthorizationService>(_ => Substitute.For<IAdminAuthorizationService>());
        services.AddScoped<NodaTime.IClock>(_ => Substitute.For<NodaTime.IClock>());

        services.AddScoped<UserService>();
        services.AddScoped<IUserService>(sp => sp.GetRequiredService<UserService>());

        services.AddScoped<RoleAssignmentService>();
        services.AddScoped<IRoleAssignmentService>(sp => sp.GetRequiredService<RoleAssignmentService>());

        services.AddScoped<ShiftManagementService>();
        services.AddScoped<IShiftManagementService>(sp => sp.GetRequiredService<ShiftManagementService>());

        services.AddScoped<TeamService>();
        services.AddScoped<ITeamService>(sp => sp.GetRequiredService<TeamService>());

        services.AddScoped<Microsoft.Extensions.Logging.ILogger<UserService>>(_ => NullLogger<UserService>.Instance);
        services.AddScoped<Microsoft.Extensions.Logging.ILogger<RoleAssignmentService>>(_ => NullLogger<RoleAssignmentService>.Instance);
        services.AddScoped<Microsoft.Extensions.Logging.ILogger<ShiftManagementService>>(_ => NullLogger<ShiftManagementService>.Instance);
        services.AddScoped<Microsoft.Extensions.Logging.ILogger<TeamService>>(_ => NullLogger<TeamService>.Instance);
        services.AddScoped<Microsoft.Extensions.Logging.ILogger<UserEmailService>>(_ => NullLogger<UserEmailService>.Instance);

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var resolve = () => scope.ServiceProvider.GetRequiredService<IUserService>();

        resolve.Should().NotThrow();
        resolve().Should().BeOfType<UserService>();
    }

    [HumansFact]
    public void IUserService_And_IEmailService_Resolve_WhenRealEmailChainIsRegistered()
    {
        var services = new ServiceCollection();
        var userStore = Substitute.For<IUserStore<User>>();

        services.AddScoped(_ => new HumansDbContext(DbOptions));
        services.AddSingleton<IMemoryCache>(_ => new MemoryCache(new MemoryCacheOptions()));

        services.AddScoped<IUserRepository>(_ => Substitute.For<IUserRepository>());
        services.AddScoped<IUserEmailRepository>(_ => Substitute.For<IUserEmailRepository>());
        services.AddScoped<IProfileRepository>(_ => Substitute.For<IProfileRepository>());
        services.AddScoped<IContactFieldRepository>(_ => Substitute.For<IContactFieldRepository>());
        services.AddScoped<ICommunicationPreferenceRepository>(_ => Substitute.For<ICommunicationPreferenceRepository>());
        services.AddScoped<IUserInfoInvalidator>(_ => Substitute.For<IUserInfoInvalidator>());
        services.AddScoped<IRoleAssignmentRepository>(_ => Substitute.For<IRoleAssignmentRepository>());
        services.AddScoped<IShiftManagementRepository>(_ => Substitute.For<IShiftManagementRepository>());
        services.AddScoped<IAuditLogService>(_ => Substitute.For<IAuditLogService>());
        services.AddScoped<INotificationEmitter>(_ => Substitute.For<INotificationEmitter>());
        services.AddScoped<ISystemTeamSync>(_ => Substitute.For<ISystemTeamSync>());
        services.AddScoped<INavBadgeCacheInvalidator>(_ => Substitute.For<INavBadgeCacheInvalidator>());
        services.AddScoped<IRoleAssignmentClaimsCacheInvalidator>(_ => Substitute.For<IRoleAssignmentClaimsCacheInvalidator>());
        services.AddScoped<ITeamRepository>(_ => Substitute.For<ITeamRepository>());
        services.AddScoped<INotificationMeterCacheInvalidator>(_ => Substitute.For<INotificationMeterCacheInvalidator>());
        services.AddScoped<IShiftAuthorizationInvalidator>(_ => Substitute.For<IShiftAuthorizationInvalidator>());
        services.AddScoped<IAdminAuthorizationService>(_ => Substitute.For<IAdminAuthorizationService>());
        services.AddScoped<IEmailOutboxRepository>(_ => Substitute.For<IEmailOutboxRepository>());
        services.AddScoped<IEmailRenderer>(_ => Substitute.For<IEmailRenderer>());
        services.AddScoped<IEmailBodyComposer>(_ => Substitute.For<IEmailBodyComposer>());
        services.AddScoped<IImmediateOutboxProcessor>(_ => Substitute.For<IImmediateOutboxProcessor>());
        services.AddScoped<IHumansMetrics>(_ => Substitute.For<IHumansMetrics>());
        services.AddScoped<ICommunicationPreferenceService>(_ => Substitute.For<ICommunicationPreferenceService>());
        services.AddScoped<NodaTime.IClock>(_ => Substitute.For<NodaTime.IClock>());
        services.AddScoped<UserManager<User>>(_ =>
            Substitute.For<UserManager<User>>(userStore, null, null, null, null, null, null, null, null));

        services.AddScoped<UserService>();
        services.AddScoped<IUserService>(sp => sp.GetRequiredService<UserService>());

        services.AddScoped<RoleAssignmentService>();
        services.AddScoped<IRoleAssignmentService>(sp => sp.GetRequiredService<RoleAssignmentService>());

        services.AddScoped<ShiftManagementService>();
        services.AddScoped<IShiftManagementService>(sp => sp.GetRequiredService<ShiftManagementService>());

        services.AddScoped<UserEmailService>();
        services.AddScoped<IUserEmailService>(sp => sp.GetRequiredService<UserEmailService>());

        services.AddScoped<OutboxEmailService>();
        services.AddScoped<IEmailService>(sp => sp.GetRequiredService<OutboxEmailService>());

        services.AddScoped<TeamService>();
        services.AddScoped<ITeamService>(sp => sp.GetRequiredService<TeamService>());

        services.AddScoped<Microsoft.Extensions.Logging.ILogger<UserService>>(_ => NullLogger<UserService>.Instance);
        services.AddScoped<Microsoft.Extensions.Logging.ILogger<RoleAssignmentService>>(_ => NullLogger<RoleAssignmentService>.Instance);
        services.AddScoped<Microsoft.Extensions.Logging.ILogger<ShiftManagementService>>(_ => NullLogger<ShiftManagementService>.Instance);
        services.AddScoped<Microsoft.Extensions.Logging.ILogger<TeamService>>(_ => NullLogger<TeamService>.Instance);
        services.AddScoped<Microsoft.Extensions.Logging.ILogger<OutboxEmailService>>(_ => NullLogger<OutboxEmailService>.Instance);
        services.AddScoped<Microsoft.Extensions.Logging.ILogger<UserEmailService>>(_ => NullLogger<UserEmailService>.Instance);

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var resolveUserService = () => scope.ServiceProvider.GetRequiredService<IUserService>();
        var resolveEmailService = () => scope.ServiceProvider.GetRequiredService<IEmailService>();

        resolveUserService.Should().NotThrow();
        resolveEmailService.Should().NotThrow();
        resolveUserService().Should().BeOfType<UserService>();
        resolveEmailService().Should().BeOfType<OutboxEmailService>();
    }

    /// <summary>
    /// Generic cycle guard. Scans every concrete class implementing
    /// <see cref="IApplicationService"/> across the Humans assemblies, maps each
    /// interface ctor parameter to its <c>IFoo → Foo</c> implementation by naming
    /// convention, and DFS-detects cycles. Edges through lazy escape hatches
    /// (<see cref="IServiceProvider"/>, <see cref="Lazy{T}"/>, <see cref="Func{T}"/>,
    /// <see cref="IEnumerable{T}"/>) are deliberately not followed — those defer
    /// resolution out of the ctor and break cycles in MS DI.
    ///
    /// This test fails fast at build time, instead of hanging at first request
    /// like the original <c>IOnboardingEligibilityQuery</c> incident, by
    /// inspecting the graph directly rather than relying on
    /// <c>ServiceProviderOptions.ValidateOnBuild</c>, which misses cycles routed
    /// through <c>sp => sp.GetRequiredService&lt;ConcreteImpl&gt;()</c>
    /// forwarder factories.
    /// </summary>
    [HumansFact]
    public void NoCircularConstructorDependencies_AcrossApplicationServices()
    {
        var assemblies = new[]
        {
            typeof(IApplicationService).Assembly,
            typeof(HumansDbContext).Assembly,
            typeof(Humans.Web.Controllers.HomeController).Assembly,
        };

        var concreteServices = assemblies
            .SelectMany(SafeGetTypes)
            .Where(t => t.IsClass && !t.IsAbstract && !t.IsGenericTypeDefinition)
            .Where(t => typeof(IApplicationService).IsAssignableFrom(t))
            .ToHashSet();

        // Interface → concrete implementation via "IFoo → Foo" naming convention,
        // restricted to types we just collected so external interface
        // implementations don't pollute the graph.
        var implByInterface = new Dictionary<Type, Type>();
        foreach (var concrete in concreteServices)
        {
            foreach (var iface in concrete.GetInterfaces())
            {
                if (!iface.Name.StartsWith("I", StringComparison.Ordinal)) continue;
                if (!string.Equals(concrete.Name, iface.Name[1..], StringComparison.Ordinal)) continue;
                implByInterface[iface] = concrete;
            }
        }

        var edges = new Dictionary<Type, HashSet<Type>>();
        foreach (var concrete in concreteServices)
        {
            var ctor = concrete.GetConstructors()
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();
            if (ctor is null) continue;

            var deps = new HashSet<Type>();
            foreach (var p in ctor.GetParameters())
            {
                var pt = p.ParameterType;
                if (IsLazyEscapeHatch(pt)) continue;
                if (pt.IsInterface && implByInterface.TryGetValue(pt, out var impl))
                {
                    deps.Add(impl);
                }
                else if (concreteServices.Contains(pt))
                {
                    deps.Add(pt);
                }
            }
            edges[concrete] = deps;
        }

        var state = new Dictionary<Type, int>();
        var cycles = new List<List<Type>>();
        foreach (var node in edges.Keys)
        {
            DfsForCycle(node, edges, state, [], cycles);
        }

        cycles.Should().BeEmpty(
            "constructor dependencies between IApplicationService implementations must form a DAG — " +
            "every edge in a cycle is a real ctor injection that MS DI will fail to resolve at first " +
            "request and (in some forwarder-factory configurations) hang instead of throw. Break cycles " +
            "by relocating the predicate/write to its rightful owner, or as a last resort by switching " +
            "one side to IServiceProvider/Lazy<T> lookup with a comment explaining why the inversion " +
            "wasn't viable. Cycles found:\n" +
            string.Join("\n", cycles.Select(c => "  " + string.Join(" -> ", c.Select(t => t.Name)))));
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }

    private static bool IsLazyEscapeHatch(Type t)
    {
        if (t == typeof(IServiceProvider)) return true;
        if (!t.IsGenericType) return false;
        var def = t.GetGenericTypeDefinition();
        return def == typeof(Lazy<>) || def == typeof(Func<>) || def == typeof(IEnumerable<>);
    }

    private static void DfsForCycle(
        Type node,
        IDictionary<Type, HashSet<Type>> edges,
        IDictionary<Type, int> state,
        List<Type> path,
        List<List<Type>> cycles)
    {
        if (state.TryGetValue(node, out var s))
        {
            if (s == 1)
            {
                var start = path.IndexOf(node);
                if (start >= 0)
                {
                    var cycle = path.GetRange(start, path.Count - start);
                    cycle.Add(node);
                    cycles.Add(cycle);
                }
            }
            return;
        }
        state[node] = 1;
        path.Add(node);
        if (edges.TryGetValue(node, out var nexts))
        {
            foreach (var next in nexts)
            {
                DfsForCycle(next, edges, state, path, cycles);
            }
        }
        path.RemoveAt(path.Count - 1);
        state[node] = 2;
    }
}
