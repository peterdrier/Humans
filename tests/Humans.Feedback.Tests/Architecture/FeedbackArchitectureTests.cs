using AwesomeAssertions;
using Humans.Users.Contracts;
using Humans.Teams.Contracts;
using Humans.Feedback.Data;
using FeedbackService = Humans.Feedback.Services.FeedbackService;

namespace Humans.Feedback.Tests.Architecture;

public class FeedbackArchitectureTests
{
    [HumansFact]
    public void FeedbackService_TakesCrossSectionServiceInterfaces()
    {
        var ctor = typeof(FeedbackService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IUserService),
            because: "Feedback resolves reporter / assignee / resolver display names via IUserService.GetUserInfosAsync — UserInfo.BurnerName implements the BurnerName-first fallback per memory/architecture/burnername-is-the-display-name.md");
        var deliveryParamTypes = typeof(Humans.Feedback.Services.FeedbackReplyDelivery)
            .GetConstructors().Single().GetParameters().Select(p => p.ParameterType);
        deliveryParamTypes.Should().Contain(typeof(IUserEmailService),
            because: "Feedback's delivery pipeline resolves the reporter's effective notification email via IUserEmailService.GetNotificationTargetEmailsAsync — no User.UserEmails navigation");
        paramTypes.Should().Contain(typeof(ITeamService),
            because: "Feedback resolves assigned-team names via the cross-section ITeamService surface — no FeedbackReport.AssignedToTeam navigation at query time");
    }

    [HumansFact]
    public void AuditEntityTypesAreLiterals()
    {
        // These are literal string values we store in the DB. Pinned so a rename can't
        // quietly change them and orphan existing audit_log rows
        // (memory/code/type-name-as-persisted-string.md).
        Humans.Feedback.Services.AuditEntityTypes.FeedbackReport.Should().Be("FeedbackReport");
    }
}
