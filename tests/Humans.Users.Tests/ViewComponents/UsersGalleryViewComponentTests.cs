using System.Security.Claims;
using AwesomeAssertions;
using Humans.Users.Contracts;
using Humans.Users.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using NSubstitute;

namespace Humans.Users.Tests.ViewComponents;

public sealed class UsersGalleryViewComponentTests
{
    [HumansFact]
    public async Task InvokeAsync_RequestAborted_PropagatesCancellation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var users = Substitute.For<IUserServiceRead>();
        users.GetUserInfoAsync(Arg.Any<Guid>(), aborted.Token)
            .Returns(_ => ValueTask.FromException<UserInfo?>(new OperationCanceledException(aborted.Token)));
        var component = new UsersGalleryViewComponent(users)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext
                {
                    HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token },
                    ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
                }
            }
        };
        component.ViewComponentContext.ViewContext.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())]));

        var act = () => component.InvokeAsync();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
