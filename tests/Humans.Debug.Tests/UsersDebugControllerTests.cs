using AwesomeAssertions;
using Humans.Debug.Controllers;
using Humans.Debug.Models;
using Humans.Testing;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NSubstitute;

namespace Humans.Debug.Tests;

public class UsersDebugControllerTests
{
    private static UserInfo MakeUserInfo(
        Guid id, string displayName, bool hasProfile, bool hasTicket)
    {
        EventParticipationInfo[] participations = hasTicket
            ? [new(Guid.NewGuid(), 2026, ParticipationStatus.Ticketed, ParticipationSource.TicketSync, null, null)]
            : [];

        return new UserInfo(
            id, displayName, false, "en", null, Instant.FromUtc(2026, 1, 1, 0, 0),
            null, null, null, null, null, false, false, null, null,
            null, null, null, [], participations, [],
            hasProfile ? UserFixtures.Profile(burnerName: displayName, firstName: "First", lastName: "Last") : null,
            []);
    }

    [HumansFact]
    public async Task Index_large_page_does_not_wrap_to_the_first_page()
    {
        var users = new[] { MakeUserInfo(Guid.NewGuid(), "Human", true, false) };
        var service = Substitute.For<IUserService>();
        service.GetAllRawUserInfosAsync(Arg.Any<CancellationToken>()).Returns(users);
        var controller = new UsersDebugController(service);
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

        var controller = new UsersDebugController(userService);

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

        var controller = new UsersDebugController(userService);

        var result = await controller.Index(page: 1, pageSize: 50, sort: "displayName", dir: "desc", ct: Xunit.TestContext.Current.CancellationToken) as ViewResult;

        var vm = (UsersDebugViewModel)result!.Model!;
        vm.Rows.Select(r => r.DisplayName).Should().Equal("Carol", "Bob", "Alice");
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

        var controller = new UsersDebugController(userService);

        var tooSmall = (UsersDebugViewModel)((ViewResult)await controller.Index(1, 1, ct: Xunit.TestContext.Current.CancellationToken)).Model!;
        tooSmall.PageSize.Should().Be(10);

        var tooBig = (UsersDebugViewModel)((ViewResult)await controller.Index(1, 9999, ct: Xunit.TestContext.Current.CancellationToken)).Model!;
        tooBig.PageSize.Should().Be(200);
    }
}
