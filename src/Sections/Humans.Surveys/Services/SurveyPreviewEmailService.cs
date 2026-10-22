using Humans.Email.Contracts;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Users.Contracts;

namespace Humans.Surveys.Services;

internal interface ISurveyPreviewEmailService : IOrchestrator
{
    Task<SurveyEmailPreviewResult> PreviewForUserAsync(Guid surveyId, Guid userId, CancellationToken ct = default);
    Task<SurveyPreviewEmailSendResult> SendToUserAsync(Guid surveyId, Guid userId, CancellationToken ct = default);
}

/// <summary>
/// Sends a side-effect-free survey invitation preview to the requesting author or Board/Admin user. It reuses
/// the production invitation template and transport but creates no invitation, response, or funnel row.
/// </summary>
internal sealed class SurveyPreviewEmailService(
    ISurveyService surveyService,
    IUserEmailService userEmailService,
    IUserService userService,
    IEmailService emailService,
    SurveysEmails emailMessages,
    IEmailPreviewService emailPreviews,
    SurveyPreviewTokenProvider previewTokens,
    ILogger<SurveyPreviewEmailService> logger) : ISurveyPreviewEmailService
{
    public async Task<SurveyEmailPreviewResult> PreviewForUserAsync(
        Guid surveyId,
        Guid userId,
        CancellationToken ct = default)
    {
        var (message, errorKey) = await BuildForUserAsync(surveyId, userId, ct);
        if (message is null)
            return new SurveyEmailPreviewResult(ErrorKey: errorKey);
        return new SurveyEmailPreviewResult(emailPreviews.RenderSystemMessage(message));
    }

    public async Task<SurveyPreviewEmailSendResult> SendToUserAsync(Guid surveyId, Guid userId, CancellationToken ct = default)
    {
        var (message, errorKey) = await BuildForUserAsync(surveyId, userId, ct);
        if (message is null)
            return new SurveyPreviewEmailSendResult(ErrorKey: errorKey);

        try
        {
            await emailService.SendAsync(message, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to queue survey {SurveyId} preview email for requesting user {UserId}",
                surveyId, userId);
            return new SurveyPreviewEmailSendResult(ErrorKey: "Surveys_PreviewQueueFailed");
        }

        logger.LogInformation(
            "Survey {SurveyId} preview email queued for requesting user {UserId}",
            surveyId, userId);
        return new SurveyPreviewEmailSendResult(message.RecipientEmail);
    }

    private async Task<(EmailMessage? Message, string? ErrorKey)> BuildForUserAsync(
        Guid surveyId,
        Guid userId,
        CancellationToken ct)
    {
        var detail = await surveyService.GetForEditAsync(surveyId, ct);
        if (detail is null)
            return (null, "Surveys_PreviewNotFound");
        var survey = detail.Editable;
        var emails = await userEmailService.GetNotificationTargetEmailsAsync([userId], ct);
        if (!emails.TryGetValue(userId, out var email))
            return (null, "Surveys_PreviewNoEmail");

        var user = await userService.GetUserInfoAsync(userId, ct);
        var name = user?.BurnerName ?? string.Empty;
        var culture = user?.PreferredLanguage.IsSupportedCultureCode() == true
            ? user.PreferredLanguage
            : survey.DefaultCulture;
        var title = survey.Title.Resolve(culture, survey.DefaultCulture);
        var customSubject = survey.InvitationEmailSubject.ResolveOptional(culture, survey.DefaultCulture);
        var customMessage = survey.InvitationEmailMessage.ResolveOptional(culture, survey.DefaultCulture);
        var token = previewTokens.Create(surveyId, culture);
        return (emailMessages.SurveyInvitation(
            userId,
            email, name, title, token, culture, customSubject, customMessage), null);
    }
}

internal sealed record SurveyEmailPreviewResult(RenderedEmailPreview? Preview = null, string? ErrorKey = null);
internal sealed record SurveyPreviewEmailSendResult(string? RecipientEmail = null, string? ErrorKey = null);
