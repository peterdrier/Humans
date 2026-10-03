using System.Security.Claims;
using Humans.Governance.Controllers;
using Humans.Governance.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute.Core;
using AwesomeAssertions;
using Humans.Consent.Contracts;
using Humans.Governance.Contracts;
using Humans.Governance.Services;
using Humans.Users.Contracts;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Governance.Tests.Services;

public sealed class GovernanceIndexServiceTests
{
    private readonly IApplicationServiceRead Applications = Substitute.For<IApplicationServiceRead>();
    private readonly ILegalDocumentService LegalDocuments = Substitute.For<ILegalDocumentService>();
    private readonly IUserServiceRead Users = Substitute.For<IUserServiceRead>();

    private GovernanceIndexService CreateService()
    {
        LegalDocuments.GetDocumentContentAsync("statutes")
            .Returns(new Dictionary<string, string>(StringComparer.Ordinal));
        Users.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<UserInfo>)[]);
        return new GovernanceIndexService(Applications, LegalDocuments, Users);
    }

    private static UserApplicationSnapshot Approved(Guid userId, LocalDate? termExpiresAt) =>
        new(Guid.NewGuid(), userId, ApplicationStatus.Approved, MembershipTier.Colaborador,
            Instant.FromUtc(2026, 3, 1, 12, 0), Instant.FromUtc(2026, 3, 8, 12, 0),
            termExpiresAt, "motivation", null, null, null);

    [HumansFact]
    public async Task GetIndexDataAsync_carries_the_stored_term_expiry()
    {
        var userId = Guid.NewGuid();
        var stored = new LocalDate(2029, 12, 31);
        Applications.GetUserApplicationsAsync(userId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<UserApplicationSnapshot>)[Approved(userId, stored)]);

        var data = await CreateService().GetIndexDataAsync(userId);

        data.ApplicationTermExpiresAt.Should().Be(stored);
    }

    [HumansFact]
    public async Task GetIndexDataAsync_leaves_the_term_expiry_null_when_none_is_stored()
    {
        var userId = Guid.NewGuid();
        Applications.GetUserApplicationsAsync(userId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<UserApplicationSnapshot>)[Approved(userId, null)]);

        var data = await CreateService().GetIndexDataAsync(userId);

        data.ApplicationTermExpiresAt.Should().BeNull();
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Index_CancelsAbandonedUserAndApplicationReads(bool cancelAfterViewer)
    {
        using var cancellation = new CancellationTokenSource();
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, DisplayName = "Member", Email = "member@example.com" }.ToUserInfo();
        var abandon = false;
        async ValueTask<UserInfo?> ReadUser(CallInfo call)
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            if (abandon && cancelAfterViewer) await cancellation.CancelAsync();
            return user;
        }
        Users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(ReadUser);
        Applications.GetUserApplicationsAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<UserApplicationSnapshot>>([]);
        });
        var controller = new GovernanceController(Users, CreateService())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            {
                RequestAborted = cancellation.Token,
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test")),
            } },
        };

        (await controller.Index()).Should().BeOfType<ViewResult>();
        abandon = true;
        if (!cancelAfterViewer) await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.Index());
    }

}
