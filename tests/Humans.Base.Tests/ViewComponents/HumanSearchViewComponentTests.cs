using AwesomeAssertions;
using Humans.Base.Models;
using Humans.Base.ViewComponents;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NSubstitute;
using Xunit;

namespace Humans.Base.Tests.ViewComponents;

public class HumanSearchViewComponentTests
{
    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prefill_UsesResolvedIdentityForNameAndHiddenValue(bool archivedId)
    {
        var userId = Guid.NewGuid();
        var requestedId = archivedId ? Guid.NewGuid() : userId;
        var users = Substitute.For<IUserServiceRead>();
        var info = UserInfo.Create(
            new User { Id = userId, BurnerName = "Member", State = UserState.Active }, [], [], [],
            UserFixtures.Profile(burnerName: "Member"), []);
        users.GetUserInfoAsync(requestedId, Arg.Any<CancellationToken>()).Returns(info);
        var component = new HumanSearchViewComponent(users)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() },
            },
        };

        var result = (ViewViewComponentResult)await component.InvokeAsync(selectedUserId: requestedId);

        var model = (HumanSearchPickerViewModel)result.ViewData!.Model!;
        model.SelectedUserId.Should().Be(userId);
        model.SelectedBurnerName.Should().Be("Member");
    }
}
