using AwesomeAssertions;
using Humans.Consent.Contracts;
using Humans.Debug.Controllers;
using Humans.Debug.Models;
using Humans.Email.Contracts;
using Humans.Testing;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NSubstitute;

namespace Humans.Debug.Tests;

public class UsersDebugControllerTests
{
    private readonly List<EventParticipationInfo> _ticketRows = [];
    private readonly ITicketService _tickets = Substitute.For<ITicketService>();

    public UsersDebugControllerTests() =>
        _tickets.GetAllParticipationsAsync(Arg.Any<CancellationToken>()).Returns(_ => _ticketRows.ToList());

    private UserInfo MakeUserInfo(
        Guid id, string displayName, bool hasProfile, bool hasTicket)
    {
        if (hasTicket)
            _ticketRows.Add(new(id, 2026, ParticipationStatus.Ticketed, ParticipationSource.TicketSync, null, null));

        return new UserInfo(
            id, displayName, false, "en", null, Instant.FromUtc(2026, 1, 1, 0, 0),
            null, null, null, null, null, null, null,
            null, null, null, [], [], [],
            hasProfile ? UserFixtures.Profile(burnerName: displayName, firstName: "First", lastName: "Last") : null,
            []);
    }

    private static IConsentService NoReviews()
    {
        var consent = Substitute.For<IConsentService>();
        consent.GetConsentReviewsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, ConsentReviewInfo>());
        return consent;
    }

    [HumansFact]
    public async Task Index_large_page_does_not_wrap_to_the_first_page()
    {
        var users = new[] { MakeUserInfo(Guid.NewGuid(), "Human", true, false) };
        var service = Substitute.For<IUserService>();
        service.GetAllRawUserInfosAsync(Arg.Any<CancellationToken>()).Returns(users);
        var controller = new UsersDebugController(service, NoReviews(), _tickets, Substitute.For<ICommunicationPreferenceService>());
        var first = (UsersDebugViewModel)((ViewResult)await controller.Index(page: 1, pageSize: 50)).Model!;
        first.Rows.Should().ContainSingle();

        var result = (UsersDebugViewModel)((ViewResult)await controller.Index(page: int.MaxValue, pageSize: 50)).Model!;
        result.Rows.Should().BeEmpty();
        result.TotalCount.Should().Be(1);
        result.Page.Should().Be(int.MaxValue);
    }

    [HumansFact]
    public async Task Index_returns_paged_rows_from_snapshot()
    {
        var users = Enumerable.Range(0, 60)
            .Select(i => MakeUserInfo(Guid.NewGuid(), $"User {i:D3}", hasProfile: i % 2 == 0, hasTicket: i % 3 == 0))
            .ToArray();

        var userService = Substitute.For<IUserService>();
        userService.GetAllRawUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<UserInfo>>(users));

        var controller = new UsersDebugController(userService, NoReviews(), _tickets, Substitute.For<ICommunicationPreferenceService>());

        var result = await controller.Index(page: 1, pageSize: 50, sort: "displayName", dir: "asc", ct: Xunit.TestContext.Current.CancellationToken) as ViewResult;

        result.Should().NotBeNull();
        var vm = result.Model.Should().BeOfType<UsersDebugViewModel>().Subject;
        vm.TotalCount.Should().Be(60);
        vm.Rows.Should().HaveCount(50);
        vm.TotalPages.Should().Be(2);
        vm.Rows[0].DisplayName.Should().Be("User 000");
    }

    [HumansFact]
    public async Task Index_sort_displayName_descending_reverses_order()
    {
        var users = new[]
        {
            MakeUserInfo(Guid.NewGuid(), "Alice", hasProfile: true, hasTicket: false),
            MakeUserInfo(Guid.NewGuid(), "Bob",   hasProfile: true, hasTicket: false),
            MakeUserInfo(Guid.NewGuid(), "Carol", hasProfile: true, hasTicket: false),
        };
        var userService = Substitute.For<IUserService>();
        userService.GetAllRawUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<UserInfo>>(users));

        var controller = new UsersDebugController(userService, NoReviews(), _tickets, Substitute.For<ICommunicationPreferenceService>());

        var result = await controller.Index(page: 1, pageSize: 50, sort: "displayName", dir: "desc", ct: Xunit.TestContext.Current.CancellationToken) as ViewResult;

        var vm = (UsersDebugViewModel)result!.Model!;
        vm.Rows.Select(r => r.DisplayName).Should().Equal("Carol", "Bob", "Alice");
    }

    [HumansFact]
    public async Task Index_hasTicket_comes_from_Tickets_participations_any_year()
    {
        var holder = MakeUserInfo(Guid.NewGuid(), "Holder", hasProfile: true, hasTicket: true);
        var other = MakeUserInfo(Guid.NewGuid(), "Other", hasProfile: true, hasTicket: false);
        var userService = Substitute.For<IUserService>();
        userService.GetAllRawUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<UserInfo>>([holder, other]));

        var result = await new UsersDebugController(userService, NoReviews(), _tickets, Substitute.For<ICommunicationPreferenceService>())
            .Index(ct: Xunit.TestContext.Current.CancellationToken) as ViewResult;

        var rows = ((UsersDebugViewModel)result!.Model!).Rows;
        rows.Single(r => r.UserId == holder.Id).HasTicket.Should().BeTrue();
        rows.Single(r => r.UserId == other.Id).HasTicket.Should().BeFalse();
    }

    [HumansFact]
    public async Task Index_clamps_pageSize_outside_10_to_200_range()
    {
        var users = Enumerable.Range(0, 5)
            .Select(i => MakeUserInfo(Guid.NewGuid(), $"User {i}", true, false))
            .ToArray();
        var userService = Substitute.For<IUserService>();
        userService.GetAllRawUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<UserInfo>>(users));

        var controller = new UsersDebugController(userService, NoReviews(), _tickets, Substitute.For<ICommunicationPreferenceService>());

        var tooSmall = (UsersDebugViewModel)((ViewResult)await controller.Index(1, 1, ct: Xunit.TestContext.Current.CancellationToken)).Model!;
        tooSmall.PageSize.Should().Be(10);

        var tooBig = (UsersDebugViewModel)((ViewResult)await controller.Index(1, 9999, ct: Xunit.TestContext.Current.CancellationToken)).Model!;
        tooBig.PageSize.Should().Be(200);
    }
}
