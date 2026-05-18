using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Services.Shifts;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Repositories.Shifts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Shifts;

/// <summary>
/// Tests for <see cref="ShiftSignupService.FilterToIncompleteOnboardingAsync"/> —
/// the coordinator-side Pending-list "Incomplete onboarding" filter chip helper.
/// </summary>
public sealed class ShiftSignupServiceFilterIncompleteOnboardingTests : ServiceTestHarness
{
    private readonly IMembershipCalculator _membership;
    private readonly ShiftSignupService _service;

    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 12, 0);

    public ShiftSignupServiceFilterIncompleteOnboardingTests()
        : base(TestNow)
    {
        var auditLog = Substitute.For<IAuditLogService>();
        _membership = Substitute.For<IMembershipCalculator>();

        var teamService = Substitute.For<ITeamService>();
        var roleAssignmentService = Substitute.For<IRoleAssignmentService>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(ITeamService)).Returns(teamService);
        serviceProvider.GetService(typeof(IRoleAssignmentService)).Returns(roleAssignmentService);

        var shiftRepo = new ShiftManagementRepository(DbFactory);
        var shiftMgmt = new ShiftManagementService(
            shiftRepo,
            auditLog,
            Substitute.For<IAdminAuthorizationService>(),
            serviceProvider,
            new MemoryCache(new MemoryCacheOptions()),
            Substitute.For<IShiftViewInvalidator>(),
            Clock,
            NullLogger<ShiftManagementService>.Instance);

        var repo = new ShiftSignupRepository(Db, Clock);
        _service = new ShiftSignupService(
            repo,
            shiftMgmt,
            _membership,
            auditLog,
            Substitute.For<INotificationService>(),
            Substitute.For<IAdminAuthorizationService>(),
            Substitute.For<IShiftViewInvalidator>(),
            serviceProvider,
            Clock,
            NullLogger<ShiftSignupService>.Instance);
    }

    [HumansFact]
    public async Task Filter_EmptyList_ReturnsEmptyWithoutMembershipCall()
    {
        var result = await _service.FilterToIncompleteOnboardingAsync([]);

        result.Should().BeEmpty();
        await _membership.DidNotReceiveWithAnyArgs()
            .GetUsersWithAllRequiredConsentsForTeamAsync(null!, Guid.Empty, CancellationToken.None);
    }

    [HumansFact]
    public async Task Filter_KeepsOnlySignupsWithoutAllRequiredConsents()
    {
        var withConsents = Guid.NewGuid();
        var withoutConsents = Guid.NewGuid();

        var signups = new List<ShiftSignup>
        {
            MakeSignup(withConsents),
            MakeSignup(withoutConsents)
        };

        _membership.GetUsersWithAllRequiredConsentsForTeamAsync(
                Arg.Any<IEnumerable<Guid>>(), SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { withConsents });

        var result = await _service.FilterToIncompleteOnboardingAsync(signups);

        result.Should().ContainSingle()
            .Which.UserId.Should().Be(withoutConsents);
    }

    [HumansFact]
    public async Task Filter_AllUsersHaveConsents_ReturnsEmpty()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var signups = new List<ShiftSignup> { MakeSignup(u1), MakeSignup(u2) };

        _membership.GetUsersWithAllRequiredConsentsForTeamAsync(
                Arg.Any<IEnumerable<Guid>>(), SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { u1, u2 });

        var result = await _service.FilterToIncompleteOnboardingAsync(signups);

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Filter_NoUsersHaveConsents_ReturnsAll()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var signups = new List<ShiftSignup> { MakeSignup(u1), MakeSignup(u2) };

        _membership.GetUsersWithAllRequiredConsentsForTeamAsync(
                Arg.Any<IEnumerable<Guid>>(), SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>());

        var result = await _service.FilterToIncompleteOnboardingAsync(signups);

        result.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task Filter_DistinctUserIdsPassedToMembership()
    {
        var sharedUser = Guid.NewGuid();
        var signups = new List<ShiftSignup>
        {
            MakeSignup(sharedUser),
            MakeSignup(sharedUser),
            MakeSignup(Guid.NewGuid())
        };

        _membership.GetUsersWithAllRequiredConsentsForTeamAsync(
                Arg.Any<IEnumerable<Guid>>(), SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>());

        await _service.FilterToIncompleteOnboardingAsync(signups);

        await _membership.Received(1).GetUsersWithAllRequiredConsentsForTeamAsync(
            // ReSharper disable once PossibleMultipleEnumeration — Arg.Is requires an expression-tree lambda.
            Arg.Is<IEnumerable<Guid>>(ids => ids.Distinct().Count() == ids.Count() && ids.Count() == 2),
            SystemTeamIds.Volunteers,
            Arg.Any<CancellationToken>());
    }

    private static ShiftSignup MakeSignup(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        ShiftId = Guid.NewGuid(),
        Status = SignupStatus.Pending,
        CreatedAt = TestNow,
        UpdatedAt = TestNow
    };
}
