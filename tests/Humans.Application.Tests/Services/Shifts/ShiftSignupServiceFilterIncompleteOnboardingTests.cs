using AwesomeAssertions;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Repositories;
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
        _membership = Substitute.For<IMembershipCalculator>();

        var teamService = Substitute.For<ITeamService>();
        var roleAssignmentService = Substitute.For<IRoleAssignmentService>();
        var serviceProvider = new ServiceLocatorBuilder()
            .With(teamService)
            .With<ITeamServiceRead>(teamService)
            .With(roleAssignmentService)
            .Build();

        var shiftRepo = new ShiftRepository(DbFactory, Db, Clock);
        var shiftMgmt = new ShiftManagementService(
            shiftRepo,
            AuditLog,
            AdminAuthorization,
            serviceProvider,
            new MemoryCache(new MemoryCacheOptions()),
            Substitute.For<IShiftViewInvalidator>(),
            Clock,
            NullLogger<ShiftManagementService>.Instance);

        var repo = new ShiftRepository(DbFactory, Db, Clock);
        _service = new ShiftSignupService(
            repo,
            Substitute.For<IVolunteerTrackingRepository>(),
            shiftMgmt,
            Substitute.For<IBurnSettingsService>(),
            _membership,
            AuditLog,
            Substitute.For<INotificationService>(),
            AdminAuthorization,
            Substitute.For<IShiftViewInvalidator>(),
            Substitute.For<IEarlyEntryInvalidator>(),
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
