using Humans.Email.Contracts;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Users.Contracts;

namespace Humans.Surveys.Services;

internal interface ISurveyPreviewEmailService : IOrchestrator
{
    Task<string> SendToUserAsync(Guid surveyId, Guid userId, CancellationToken ct = default);
}

/// <summary>
/// Sends a side-effect-free survey invitation preview to the requesting Board/Admin user. It reuses
/// the production invitation template and transport but creates no invitation, response, or funnel row.
/// </summary>
internal sealed class SurveyPreviewEmailService(
    ISurveyService surveyService,
    IUserEmailService userEmailService,
    IUserServiceRead userService,
    IEmailService emailService,
    IEmailMessageFactory emailMessages,
    SurveyPreviewTokenProvider previewTokens,
    ILogger<SurveyPreviewEmailService> logger) : ISurveyPreviewEmailService
{
    public async Task<string> SendToUserAsync(Guid surveyId, Guid userId, CancellationToken ct = default)
    {
        var detail = await surveyService.GetForEditAsync(surveyId, ct)
            ?? throw new InvalidOperationException("Survey not found.");
        var survey = detail.Editable;
        var emails = await userEmailService.GetNotificationTargetEmailsAsync([userId], ct);
        if (!emails.TryGetValue(userId, out var email))
            throw new InvalidOperationException("Your account has no notification email.");

        var user = await userService.GetUserInfoAsync(userId, ct);
        var name = user?.BurnerName ?? string.Empty;
        var culture = user?.PreferredLanguage.IsSupportedCultureCode() == true
            ? user.PreferredLanguage
            : survey.DefaultCulture;
        var title = survey.Title.Resolve(survey.DefaultCulture, survey.DefaultCulture);
        var token = previewTokens.Create(surveyId, culture);
        var message = emailMessages.SurveyInvitation(email, name, title, token, culture);

        try
        {
            await emailService.SendAsync(message, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to queue survey {SurveyId} preview email for requesting user {UserId}",
                surveyId, userId);
            throw new InvalidOperationException("The survey preview email could not be queued.", ex);
        }

        logger.LogInformation(
            "Survey {SurveyId} preview email queued for requesting user {UserId}",
            surveyId, userId);
        return email;
    }
}
