using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Controllers;
using Humans.Web.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NSubstitute;

namespace Humans.Web.Tests.Controllers;

public class UsersAdminDebugControllerTests
{
    private static UserInfo MakeUserInfo(
        Guid id, string displayName, bool hasProfile, bool hasTicket)
    {
        var participations = hasTicket
            ? new[]
            {
                new EventParticipation
                {
                    Id = Guid.NewGuid(),
                    UserId = id,
                    Year = 2026,
                    Status = ParticipationStatus.Ticketed,
                    Source = ParticipationSource.TicketSync,
                }
            }
            : Array.Empty<EventParticipation>();

        Profile? profile = hasProfile
            ? new Profile
            {
                Id = Guid.NewGuid(),
                UserId = id,
                BurnerName = "Burner",
                FirstName = "First",
                LastName = "Last",
                CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            }
            : null;

        return UserInfo.Create(
            user: new User
            {
                Id = id,
                DisplayName = displayName,
                PreferredLanguage = "en",
                CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                GoogleEmailStatus = GoogleEmailStatus.Unknown,
            },
            userEmails: [],
            eventParticipations: participations,
            externalLogins: [],
            profile: profile,
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);
    }

    [HumansFact]
    public async Task Index_returns_paged_rows_from_snapshot()
    {
        var users = Enumerable.Range(0, 60)
            .Select(i => MakeUserInfo(Guid.NewGuid(), $"User {i:D3}", hasProfile: i % 2 == 0, hasTicket: i % 3 == 0))
            .ToArray();

        var userService = Substitute.For<IUserService>();
        userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<UserInfo>>(users));

        var controller = new UsersAdminDebugController(userService);

        var result = await controller.Index(page: 1, pageSize: 50, sort: "displayName", dir: "asc") as ViewResult;

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
        userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<UserInfo>>(users));

        var controller = new UsersAdminDebugController(userService);

        var result = await controller.Index(page: 1, pageSize: 50, sort: "displayName", dir: "desc") as ViewResult;

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
        userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<UserInfo>>(users));

        var controller = new UsersAdminDebugController(userService);

        var tooSmall = (UsersDebugViewModel)((ViewResult)await controller.Index(1, 1, "displayName", "asc")).Model!;
        tooSmall.PageSize.Should().Be(10);

        var tooBig = (UsersDebugViewModel)((ViewResult)await controller.Index(1, 9999, "displayName", "asc")).Model!;
        tooBig.PageSize.Should().Be(200);
    }
}
