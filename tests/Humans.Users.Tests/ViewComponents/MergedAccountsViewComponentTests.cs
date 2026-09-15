using AwesomeAssertions;
using Humans.Users.Contracts;
using Humans.Users.Models;
using Humans.Users.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NodaTime;
using NSubstitute;

namespace Humans.Users.Tests.ViewComponents;

public class MergedAccountsViewComponentTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 5, 20, 12, 0);

    [HumansFact]
    public async Task NeverMerged_RendersNothing()
    {
        var id = Guid.NewGuid();

        var result = await InvokeWith(id, Row(id));

        result.Should().BeOfType<ContentViewComponentResult>(
            "an admin page invokes this unconditionally, so a clean account renders no banner");
    }

    [HumansFact]
    public async Task UnknownUser_RendersNothing()
    {
        var result = await InvokeWith(Guid.NewGuid());

        result.Should().BeOfType<ContentViewComponentResult>();
    }

    [HumansFact]
    public async Task Tombstone_NamesTheSurvivorItWasFoldedInto()
    {
        var tombstone = Guid.NewGuid();
        var survivor = Guid.NewGuid();

        var model = await ModelFrom(
            tombstone,
            Row(tombstone, mergedTo: survivor, mergedAt: Now),
            Row(survivor, name: "Survivor"));

        model.MergedInto.Should().NotBeNull();
        model.MergedInto!.UserId.Should().Be(survivor);
        model.MergedInto.DisplayName.Should().Be("Survivor");
        model.MergedAt.Should().Be(Now.ToDateTimeUtc());
        model.MergedIn.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Survivor_ListsTheWholeChainFoldedIntoIt_OldestFirst()
    {
        var survivor = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var firstMergedAt = Now.Minus(Duration.FromDays(30));

        // A→B→C: the survivor's stamped list is transitive, so both ids are named here.
        var model = await ModelFrom(
            survivor,
            Row(survivor, name: "Survivor", mergedUserIds: [first, second]),
            Row(first, name: "First", mergedTo: second, mergedAt: firstMergedAt),
            Row(second, name: "Second", mergedTo: survivor, mergedAt: Now));

        model.MergedInto.Should().BeNull();
        model.MergedIn.Select(r => r.UserId).Should().Equal([first, second]);
        model.MergedIn.Select(r => r.DisplayName).Should().Equal(["First", "Second"]);
        model.MergedIn[0].MergedAt.Should().Be(firstMergedAt.ToDateTimeUtc());
    }

    [HumansFact]
    public async Task MergedAwayRowNoLongerReadable_StillLinksTheId()
    {
        var survivor = Guid.NewGuid();
        var erased = Guid.NewGuid();

        // GDPR erasure can take the row out from under the stamped list; the widget
        // must still show that something was folded in rather than drop it silently.
        var model = await ModelFrom(survivor, Row(survivor, mergedUserIds: [erased]));

        model.MergedIn.Should().ContainSingle();
        model.MergedIn[0].UserId.Should().Be(erased);
        model.MergedIn[0].DisplayName.Should().BeNull();
    }

    [HumansFact]
    public async Task ReadsRawRows_NeverTheResolvingOnes()
    {
        var tombstone = Guid.NewGuid();
        var survivor = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        Stub(users, Row(tombstone, mergedTo: survivor, mergedAt: Now), Row(survivor));

        await Sut(users).InvokeAsync(tombstone);

        // The resolving reads answer a tombstone id with the survivor — the one row
        // this widget exists to show.
        await users.DidNotReceive().GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await users.DidNotReceive().GetAllUserInfosAsync(Arg.Any<CancellationToken>());
    }

    private static UserInfo Row(
        Guid id, string? name = null, Guid? mergedTo = null, Instant? mergedAt = null,
        IReadOnlyList<Guid>? mergedUserIds = null)
    {
        var user = new User
        {
            Id = id,
            DisplayName = name ?? "Test Human",
            PreferredLanguage = "en",
            CreatedAt = Now,
            MergedToUserId = mergedTo,
            MergedAt = mergedAt,
        };
        var info = UserInfo.Create(user, [], [], [], null, []);
        return mergedUserIds is null ? info : info with { MergedUserIds = mergedUserIds };
    }

    private static void Stub(IUserService users, params UserInfo[] rows)
    {
        users.GetAllRawUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<UserInfo>>(rows));
        foreach (var row in rows)
        {
            users.GetRawUserInfoAsync(row.Id, Arg.Any<CancellationToken>())
                .Returns(new ValueTask<UserInfo?>(row));
        }
    }

    private static MergedAccountsViewComponent Sut(IUserService users) =>
        new(users)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() },
            },
        };

    private static async Task<IViewComponentResult> InvokeWith(Guid userId, params UserInfo[] rows)
    {
        var users = Substitute.For<IUserService>();
        Stub(users, rows);
        return await Sut(users).InvokeAsync(userId);
    }

    private static async Task<MergedAccountsViewModel> ModelFrom(Guid userId, params UserInfo[] rows)
    {
        var result = await InvokeWith(userId, rows) as ViewViewComponentResult;
        return (MergedAccountsViewModel)result!.ViewData!.Model!;
    }
}
