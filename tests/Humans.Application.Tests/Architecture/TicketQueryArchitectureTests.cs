using AwesomeAssertions;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Tickets;
using Humans.Infrastructure.Repositories.Tickets;
using Humans.Infrastructure.Services.Tickets;
using TicketQueryService = Humans.Application.Services.Tickets.TicketQueryService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for the Tickets
/// query surface — migrated per issue nobodies-collective/Humans#545 sub-task
/// #545a, decorator lifted per T-07.
///
/// <para>
/// The inner <see cref="TicketQueryService"/> lives in Application, goes
/// through <see cref="ITicketRepository"/>, and never imports EF types or
/// <c>IMemoryCache</c>. The Singleton <see cref="CachingTicketQueryService"/>
/// decorator (Infrastructure) owns the per-order <c>TicketOrderInfo</c>
/// projection and the per-user short-TTL entries; it is the only impl that
/// touches <c>IMemoryCache</c> in this section.
/// </para>
/// </summary>
public class TicketQueryArchitectureTests
{
    // ── TicketQueryService (inner) ───────────────────────────────────────────

    [HumansFact]
    public void TicketQueryService_IsSealed()
    {
        typeof(TicketQueryService).IsSealed.Should().BeTrue(
            because: "Application-layer services are sealed to prevent ad-hoc subclassing; new behavior belongs on the interface");
    }

    [HumansFact]
    public void TicketQueryService_TakesRepository()
    {
        var ctor = typeof(TicketQueryService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ITicketRepository),
            because: "all ticket-table access must flow through ITicketRepository");
    }

    [HumansFact]
    public void TicketQueryService_HasNoIMemoryCacheConstructorParameter()
    {
        var ctor = typeof(TicketQueryService).GetConstructors().Single();
        var cachingParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching.Memory", StringComparison.Ordinal));

        cachingParam.Should().BeNull(
            because: "the inner TicketQueryService is cache-free per T-07; CachingTicketQueryService owns the projection and the per-user short-TTL entries");
    }

    [HumansFact]
    public void TicketQueryService_RoutesCrossSectionReadsThroughServices()
    {
        var ctor = typeof(TicketQueryService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        // Cross-section reads go through the owning services, not other repositories.
        paramTypes.Should().Contain(typeof(IBudgetService));
        paramTypes.Should().Contain(typeof(ICampaignService));
        paramTypes.Should().Contain(typeof(IUserService));
        paramTypes.Should().Contain(typeof(IUserEmailService));
        paramTypes.Should().Contain(typeof(ITeamServiceRead));
        paramTypes.Should().Contain(typeof(IShiftManagementService));
    }

    [HumansFact]
    public void TicketQueryService_TakesNoOtherSectionRepository()
    {
        var ctor = typeof(TicketQueryService).GetConstructors().Single();
        var otherRepos = ctor.GetParameters()
            .Where(p => typeof(IUserRepository).IsAssignableFrom(p.ParameterType) ||
                        typeof(IProfileRepository).IsAssignableFrom(p.ParameterType) ||
                        typeof(IUserEmailRepository).IsAssignableFrom(p.ParameterType) ||
                        typeof(IApplicationRepository).IsAssignableFrom(p.ParameterType))
            .ToList();

        otherRepos.Should().BeEmpty(
            because: "cross-section reads go through the owning service, not another section's repository (design-rules §2c)");
    }

    // ── CachingTicketQueryService (decorator) ────────────────────────────────

    [HumansFact]
    public void CachingTicketQueryService_IsSealed()
    {
        typeof(CachingTicketQueryService).IsSealed.Should().BeTrue(
            because: "the caching decorator is terminal — section-internal logic stays on the inner service and the projection layout is private to the decorator");
    }

    [HumansFact]
    public void CachingTicketQueryService_ImplementsITicketQueryService()
    {
        typeof(ITicketQueryService).IsAssignableFrom(typeof(CachingTicketQueryService))
            .Should().BeTrue();
    }

    [HumansFact]
    public void CachingTicketQueryService_ImplementsITicketCacheInvalidator()
    {
        typeof(ITicketCacheInvalidator).IsAssignableFrom(typeof(CachingTicketQueryService))
            .Should().BeTrue(
                because: "the decorator owns the cache layout, so it owns the invalidation seam that external write-side callers (sync, merge fold) poke");
    }

    [HumansFact]
    public void CachingTicketQueryService_IsTheOnlyImplementationOfITicketCacheInvalidator()
    {
        // Only the Singleton decorator may implement the invalidator seam. If
        // a second impl ever appears in src/, the decorator stops being the
        // single cache-truth — write-side callers can no longer rely on a
        // poke reaching every key. Catches accidental "helper" classes that
        // claim the interface and silently do partial eviction.
        var assemblies = new[]
        {
            typeof(TicketQueryService).Assembly,            // Humans.Application
            typeof(CachingTicketQueryService).Assembly,     // Humans.Infrastructure
        };

        var impls = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => !t.IsInterface
                        && !t.IsAbstract
                        && typeof(ITicketCacheInvalidator).IsAssignableFrom(t))
            .ToList();

        impls.Should().ContainSingle(
            because: "ITicketCacheInvalidator is the one-way seam owned by the Singleton decorator; a second impl would split cache truth")
            .Which.Should().Be(typeof(CachingTicketQueryService));
    }

    [HumansFact]
    public void TicketQueryService_InvalidateMethodsAreNoOps()
    {
        // The inner's invalidation methods exist only to satisfy the
        // ITicketQueryService surface contract — the decorator intercepts
        // in normal DI resolution and owns the cache. If someone resolves
        // the keyed inner directly and calls these, eviction silently
        // doesn't happen, so the bodies MUST stay empty (any added work
        // would imply the inner has hidden state to drop, which it doesn't).
        //
        // An empty C# method body compiles to IL containing only `nop` (0x00,
        // debug only) and a single trailing `ret` (0x2A). Any other opcode
        // means there's real work in the body.
        const byte Nop = 0x00;
        const byte Ret = 0x2A;
        var methodNames = new[] { nameof(TicketQueryService.InvalidateAfterTransfer), nameof(TicketQueryService.InvalidateAfterContactImport) };

        foreach (var name in methodNames)
        {
            var method = typeof(TicketQueryService).GetMethod(name);
            method.Should().NotBeNull(because: $"{name} is on the ITicketQueryService surface");

            var body = method.GetMethodBody();
            body.Should().NotBeNull(because: $"{name} is a concrete method on the inner");

            var il = body.GetILAsByteArray();
            il.Should().NotBeNull();
            il.Should().OnlyContain(b => b == Nop || b == Ret,
                because: $"TicketQueryService.{name} must remain a no-op (only nop/ret opcodes) — the decorator owns invalidation; any non-empty body implies hidden inner cache state");
        }
    }

    [HumansFact]
    public void CachingTicketQueryService_HasCurrentEventTicketAsync_DoesNotCallRepositoryOrFilter()
    {
        // §5a rule 4: decorators contain zero business logic. The previous
        // body of HasCurrentEventTicketAsync on the decorator did a live
        // GetSyncStateAsync round-trip AND applied payment/attendee-status
        // filtering — both belong on the inner. This pin walks the IL of
        // the decorator method and confirms none of its referenced methods
        // resolve to ITicketRepository (any method on it) — the only call
        // surface should be the inner ITicketQueryService.
        var method = typeof(CachingTicketQueryService)
            .GetMethod(nameof(CachingTicketQueryService.HasCurrentEventTicketAsync));
        method.Should().NotBeNull();

        var body = method.GetMethodBody();
        body.Should().NotBeNull();

        var il = body.GetILAsByteArray()!;
        var module = method.Module;

        // Walk the IL collecting any method-token references. Tokens for
        // method-call opcodes (call 0x28, callvirt 0x6F, newobj 0x73, ldftn,
        // ldvirtftn) are followed by a 4-byte metadata token. We resolve
        // each token and check the declaring type.
        var repositoryCalls = new List<string>();
        for (var i = 0; i < il.Length; i++)
        {
            var op = il[i];
            var isCall = op is 0x28 or 0x6F or 0x73;
            var isLdftn = op == 0xFE && i + 1 < il.Length && (il[i + 1] == 0x06 || il[i + 1] == 0x07);

            if (isCall && i + 4 < il.Length)
            {
                var token = BitConverter.ToInt32(il, i + 1);
                try
                {
                    var resolved = module.ResolveMethod(token);
                    if (resolved?.DeclaringType is { } dt &&
                        typeof(ITicketRepository).IsAssignableFrom(dt))
                    {
                        repositoryCalls.Add($"{resolved.DeclaringType.Name}.{resolved.Name}");
                    }
                }
                catch
                {
                    // Token couldn't be resolved (generic context); ignore.
                }
                i += 4;
            }
            else if (isLdftn && i + 5 < il.Length)
            {
                i += 5;
            }
        }

        repositoryCalls.Should().BeEmpty(
            because: "HasCurrentEventTicketAsync on the decorator must be a pure pass-through (§5a rule 4); active-event resolution and payment/attendee-status filtering belong on the inner TicketQueryService, not the cache wrapper");
    }

    [HumansFact]
    public void TicketSyncService_TakesITicketCacheInvalidator()
    {
        // The Tickets-section write site (sync job + IUserMerge fold) must
        // hold a reference to the invalidator so every commit poke reaches
        // the decorator's projection. Mirrors the Shifts fan-in invariant
        // pinned in ShiftViewArchitectureTests.
        var ctor = typeof(TicketSyncService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ITicketCacheInvalidator),
            because: "TicketSyncService drives InvalidateAll (post-sync) and InvalidateAfterUserMerge (ReassignAsync) — it must take the seam through DI so the decorator's projection drops");
    }

    // ── ITicketRepository ────────────────────────────────────────────────────

    [HumansFact]
    public void TicketRepository_IsSealed()
    {
        var repoType = typeof(TicketRepository);

        repoType.IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }
}
